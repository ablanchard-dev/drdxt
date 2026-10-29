using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>Le scan : son déroulé, ses mesures (réseau, processeur, mémoire, jeu) et la publication du résultat.</summary>
public sealed partial class ScanViewModel
{
    /// <summary>Zero levier recommande se dit, au lieu d'un compteur a 0 et d'une liste vide.</summary>
    public bool AucuneRecommandation => Recommendations.Count == 0;
    private void NotifierAucuneRecommandation(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(AucuneRecommandation));
    public string LabelAucuneRecommandation => _localize("Scan.Recommendations.None");
    public string LabelScanning => _localize("Scan.State.Scanning");
    // Titre et texte du bandeau principal, à l'attente.
    public string LabelIdleHeadlineLead => _localize("UI.Idle.HeadlineLead");
    public string LabelIdleHeadlineAccent => _localize("UI.Idle.HeadlineAccent");
    public string LabelProductPromise => _localize("Product.Promise");
    public string LabelIdleHeroTitle => _localize("UI.Idle.HeroTitle");
    public string LabelIdleHeroBody => _localize("UI.Idle.HeroBody");
    public string LabelTrustLocal => _localize("UI.Trust.Local");
    public string LabelTrustData => _localize("UI.Trust.Data");

#if DEBUG
    /// <summary>Debug seulement (argument « xmp-off ») : voir le guide BIOS d'un XMP coupé.</summary>
    internal static bool SimulerXmpOffPourLeBios;
#endif

    /// <summary>
    /// Remplit la liste d'attente « Le scan va examiner… ». Affiche tout de suite les libellés
    /// traduits avec un tiret en attente, puis détecte le matériel hors du thread UI et met les
    /// vrais modèles quand ils sont prêts. La vue peut l'appeler sans l'attendre, une fois la
    /// fenêtre affichée.
    /// </summary>
    public async Task LoadIdleComponentsAsync()
    {
        PopulateComponents(hw: null);

        // Lancée sans attente : une détection qui lève laisse les composants « — » (non lus), et l'exception est observée.
        var stockage = LireStockageAsync();
        try
        {
            var hwResult = await Task.Run(async () => await _detector.DetectAsync()).ConfigureAwait(true);
            await stockage.ConfigureAwait(true);
            if (State == ScanState.Idle) PopulateComponents(hwResult.IsOk ? hwResult.Value : null);
        }
        catch (Exception)
        {
            await stockage.ConfigureAwait(true);
            if (State == ScanState.Idle) PopulateComponents(hw: null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        // Les sondes parlent a WMI, au registre, a des pilotes et a des services, qui levent au lieu de refuser. Sans cette
        // garde, l'exception atteindrait le filet fatal de App.xaml.cs et fermerait l'App en plein scan. Un refus propre
        // (`Result.Fail`) garde son message ; ceci ne rattrape que l'imprevu, et le detail part au journal, pas a l'ecran.
        if (ConfierAUneInstanceElevee()) return;
        var numero = ++_numeroDeScan;
        try
        {
            await ScanCoeurAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Scan interrupted by an exception: {0}", ex);
            ErrorMessage = _localize("Scan.Error.Inattendu");
            State = ScanState.Failed;
        }
        finally
        {
            _etapeEnCours = 0;
            // Finie, la jauge reste pleine un instant ; interrompue, elle part tout de suite.
            if (State is ScanState.Failed) JaugeVisible = false;
            else _ = MasquerLaJaugeAsync(numero);
        }
    }

    private async Task ScanCoeurAsync()
    {
        ReinitialiserPourUnScan();
        PolesLus = 0;

        Etape(1, "Scan.Etape.Materiel");
        var stockage = LireStockageAsync();
        var hwResult = await _detector.DetectAsync();
        await stockage;
        if (!hwResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Detection");
            State = ScanState.Failed;
            return;
        }

        var hw = hwResult.Value!;
        _materielLu = hw;
        PopulateComponents(hw);
        // La version du pilote graphique, demandée au fabricant pendant le reste de l'analyse.
        _verificationPilote = _verifierPilote is { } verifier ? verifier(hw.Gpus) : null;

        Etape(2, "Scan.Etape.Reglages");
        var stResult = await _settings.ReadAsync(hw);
        if (!stResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Settings");
            State = ScanState.Failed;
            return;
        }

        var st = stResult.Value!;

        // Le BIOS LU remplace l'inference quand il est lisible et que Windows ne le
        // contredit pas. Sinon `st` reste tel quel : l'inference, avec ses reserves.
        if (_biosNvram is not null)
            st = DrDxt.Bios.NvramBiosSettingsReader.Appliquer(
                st, await _biosNvram.ReadAsync(hw.Board.Product, hw.Bios.Version));
        _reglagesBiosLus = st.BiosLu;
        // Les écarts à l'usine, lus dans le magasin d'usine du firmware ; null quand la carte n'est pas dans la table.
        _ecartsUsine = _biosNvram is null ? null : await _biosNvram.LireEcartsAsync(hw.Board.Product, hw.Bios.Version);
        _reglagesLus = st;
        try { RetirerLeviersVolatilsRendus(); }
        catch (Exception) { /* un journal non nettoyé ne fait pas échouer le scan */ }
        // Le docteur n'a pas de helper pour lire le BIOS ; il relit ce que le scan vient d'en lire. Réécrit à
        // Chaque scan, vide quand rien n'est lu : un relevé d'avant une mise à jour du BIOS ne survit pas.
        FichierBiosLu?.WriteAtomic(System.Text.Json.JsonSerializer.Serialize(
            new DrDxt.Detection.Cpu.ChaleurEnPartie.BiosLu(Horloge(), st.BiosLu ?? [])));

        // L'ordre des etapes est une contrainte. Le verdict de retour BIOS lit la reference d'avant le voyage ; le
        // rafraichissement remplace une reference des que la frequence a change (ce que fait XMP off -> on). Rafraichir
        // d'abord ferait comparer l'apres a l'apres : « aucune difference mesurable » sur un vrai +79 %.
        // Verrouille par ScanMeasuresMemoryTests.La_reference_d_AVANT_le_BIOS_survit_au_scan_qui_la_relit.
        Etape(3, "Scan.Etape.Bios");
        var abMemoire = await PopulateBiosLoopAsync(st, hw);

        // Le reseau passe avant le banc processeur, qui sature tous les cœurs rapides pendant plus de
        // quarante secondes : mesurer la gigue apres lui attribuerait au reseau une instabilite que
        // le produit vient lui-meme de fabriquer.
        Etape(4, "Scan.Etape.ReseauProcesseur");
        await MesurerReseauEtProcesseurAsync();
        // Hors du bloc processeur, une panne du banc ne saute plus le relevé.
        try { await ReleverReglagesSuivisAsync(EtatsSuivis(_reglagesLus, _materielLu, LireInstantReplay?.Invoke(), _reglagesBiosLus), Horloge()); }
        catch (Exception) { /* un suivi raté ne fait pas échouer le scan */ }
        AllumerPole("Cpu");

        // La memoire remplace la reference : elle reste apres PopulateBiosLoopAsync, ci-dessus.
        Etape(5, "Scan.Etape.Memoire");
        st = await MesurerLaMemoireAsync(st, hw, abMemoire);
        AllumerPole("Ram");

        if (hw.Chassis is ChassisType.Laptop or ChassisType.Handheld)
            GatingNote = _localize("Scan.Gating.Note");

        Etape(6, "Scan.Etape.EnJeu");
        var (baseline, degraded) = await MesurerEnJeuAsync();
        AllumerPole("Gpu");
        ApplyBaseline(baseline);
        JournaliserLaMesure(baseline);
        PublierLesLibellesDeMesure(baseline);

        Etape(7, "Scan.Etape.Analyse");
        var scoreResult = _scorer.Score(hw, st, baseline);
        if (!scoreResult.IsOk)
        {
            ErrorMessage = _localize("Scan.Error.Analyse");
            State = ScanState.Failed;
            return;
        }

        var scanResult = scoreResult.Value!;
        PublierLeResultat(scanResult, degraded);
        // Le goulot mesure entre dans `st` ici, et ce `st`-la sert aussi au conseiller BIOS, plus bas.
        st = PeuplerLesRecommandations(hw, st, baseline, scanResult);

        // Affiche les constats firmware du docteur BIOS (XMP/EXPO coupé est le plus gros gain réel,
        // et il vit dans le BIOS, pas dans un levier logiciel). Guidage seul : jamais ajouté au lot
        // appliqué, le joueur les change lui-même dans le BIOS. Puis construit le guide personnalisé
        // (le « comment » exact) à partir des mêmes constats bruts.
#if DEBUG
        var biosFindings = PopulateBiosFindings(hw, SimulerXmpOffPourLeBios ? st with { XmpExpo = TriState.Off } : st);
#else
        var biosFindings = PopulateBiosFindings(hw, st);
#endif
        PopulateBiosGuide(hw, biosFindings, st);

        // L'audit de sante, pure lecture (registre, journal d'evenements, bcdedit, WMI). Il vit
        // ici et pas dans le banc de mesure parce que la frontiere est le cout : ces familles ne
        // demandent qu'une lecture, la memoire et le reseau exigent une mesure qui dure.
        Etape(8, "Scan.Etape.Audit");
        await PopulateRepairAuditAsync();
        // L'audit de santé lit ensemble le stockage (usure, SMART) et Windows : leurs deux pôles s'allument ensemble.
        AllumerPole("Ssd", "Windows", "Latency", "Network", "Firmware");

        ConstruireCarte();
        if (_etapeEnCours > 0) _dureesMesurees[_etapeEnCours - 1] = _chronoEtape.Elapsed.TotalSeconds;
        if (_dureesMesurees.All(d => d > 0)) EnregistrerDureesDesEtapes?.Invoke((double[])_dureesMesurees.Clone());
        await JugerCeQuiResteAPoserAsync().ConfigureAwait(true);
        ProgressionDuScan = 1;
        PlafondDuScan = 1;
        VitesseDuScan = 0;

        State = degraded ? ScanState.Degraded
              : DejaReglee(scanResult) ? ScanState.AlreadyOptimal
              : ScanState.Done;
    }

    private void ReinitialiserPourUnScan()
    {
        _posesDepuisLeScan.Clear();
        State = ScanState.Scanning;
        Findings.Clear();
        ProvenProblemCount = 0;
        // Le verdict du scan précédent ne reste pas au-dessus d'un scan qui échoue.
        ProblemSummary = string.Empty;
        SubtitleText = string.Empty;
        DateDuScan = null;
        ErrorMessage = null;
        GatingNote = null;
        DegradedNote = null;
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
        ResultatOptimisation = null;
        AvgFpsDisplay = Dash;
        P1LowDisplay = Dash;
        MarginDisplay = Dash;
        Recommendations.Clear();
        Domaines.Clear();
        PointsAFaire.Clear();
        DomaineSelectionne = null;
        OnPropertyChanged(nameof(ADesDomaines));
        AnnoncerSatellites();
        BiosFindings.Clear();
        RepairFindings.Clear();
        RepairSummary = null;
        BiosGuide = null;
        BiosLoop = null;
        MemoryAb = null;
        _consumption = null;
        OnPropertyChanged(nameof(ApercuOptimisation));
        _scannedBaseline = null;
        _goulotScan = null;
        _pressionThreads = GameThreadPressure.Unknown;
        _constatPertesReseau = null;
        _constatBufferbloat = null;
        _constatDpc = null;
        _constatRoute = null;
        _constatMemoireEnPartie = null;
    }

    // Le reseau, puis le processeur : une panne de l'un ou de l'autre ne fait jamais echouer le scan.
    private async Task MesurerReseauEtProcesseurAsync()
    {
        if (_cpuBaseline is not null && _machineId is not null)
        {
            try
            {
                var banc = _cpuBaseline.MesureraPourScan(_machineId(), _gameLocator.JeuOuvert(), ReglageDePuissanceLu(_reglagesBiosLus));
                Prevoir(4, DureeReseau + (banc ? DureeBancProcesseur : 0));
            }
            catch (Exception) { /* la jauge garde sa prévision */ }
        }
        // D'ou vient l'instabilite du reseau.
        //
        // Ce bloc doit rester avant le banc CPU, juste en dessous. Le banc sature tous les
        // cœurs rapides pendant plus de quarante secondes : mesurer la gigue ICMP apres lui, ce
        // serait attribuer au reseau une instabilite que le produit vient lui-meme de fabriquer.
        // Meme garde que partout ailleurs : une panne de ce cote ne fait pas echouer le scan.
        if (_attributionReseau is not null)
        {
            try
            {
                NetworkPathLabel = await _attributionReseau(CancellationToken.None);
            }
            catch
            {
                NetworkPathLabel = null;
            }
        }

        if (_cpuBaseline is not null && _machineId is not null)
        {
            try
            {
                // Hors du thread UI : la premiere mesure charge tous les cœurs rapides pendant plus
                // de quarante secondes. Meme motif que le compte de canaux memoire, et meme garde :
                // une panne de ce cote ne doit jamais faire echouer le scan.
                var jeuEnCours = _gameLocator.JeuOuvert();
                // Le réglage de puissance lu voyage avec la mesure ; changé depuis, on remesure.
                var reglage = ReglageDePuissanceLu(_reglagesBiosLus);
                var refAvant = _cpuBaseline.Reference(_machineId());
                var deficit = await Task.Run(() => _cpuBaseline.DeficitPourScan(_machineId(), jeuEnCours, reglage));
                NoterChangementBios(refAvant?.ReglageBios, _cpuBaseline.Reference(_machineId())?.ReglageBios, refAvant?.TakenUtc);

                // La référence processeur de la machine de référence (11/09 17:46, ±7,4 %) précède de
                // cinq minutes une capture de Warzone : prise pendant la partie, comme celle de la mémoire. La
                // réserve voyage avec le verdict : le rapport envoyé au support la porte comme l'écran.
                if (deficit?.Reference is { } prise)
                    deficit = deficit with { Reserve = MentionPriseEnJeu("Cpu.PriseEnJeu", prise.TakenUtc, prise.JeuFermeVerifie) };
                _dernierDeficitCpu = deficit;
                CpuSustainedLabel = PhraseProcesseur(deficit, _reglagesBiosLus);
                CpuSauteJeu = jeuEnCours && deficit is null;
                ErreurRemesureCpu = null;
            }
            catch
            {
                CpuSustainedLabel = null;
            }
        }
    }

    // La mesure memoire. Sans elle, MemoryBench serait un instrument que le produit n'appelle
    // jamais. Elle ne tourne qu'au premier scan (decision produit ; le service relit ensuite la
    // reference enregistree) et sur un fil de travail, parce que parcourir 384 Mo bloquerait
    // l'interface. Une mesure rejetee rend 0 : le scan continue sans rien affirmer.
    // Elle ne tourne pas apres un A/B refuse : l'ecran vient de dire au client « relance au repos »,
    // et le marqueur BIOS est deja consomme, donc la seule chose qui rend ce conseil applicable est
    // la reference d'avant. La rafraichir rendrait la comparaison impossible.
    private async Task<SettingsState> MesurerLaMemoireAsync(SettingsState st, HardwareInventory hw, MemoryAbOutcome abMemoire)
    {
        if (abMemoire != MemoryAbOutcome.Refusee && _memoryBaseline is not null && _machineId is not null)
        {
            try
            {
                var jeuEnCours = _gameLocator.JeuOuvert();
                Prevoir(5, _memoryBaseline.MesureraPourScan(_machineId(), hw.Memory, jeuEnCours) ? DureeBancMemoire : DureeSansBanc);
                var canaux = await Task.Run(
                    () => _memoryBaseline.ProvenChannelsForScan(_machineId(), hw.Memory, jeuEnCours));
                st = st with { MemoryProvenChannels = canaux };
                st = st with
                {
                    MemoryUnSeulCanalMesure = _memoryBaseline.MesureCourante(_machineId(), hw.Memory) is { } mesuree
                        && DrDxt.Measurement.Memory.MemoryInference.UnSeulCanalProbable(mesuree.Measurement.BandwidthMultiFilGBs, hw.Memory),
                };

                // On relit le magasin, on ne remesure pas. Rien d'enregistre (mesure rejetee, banc indisponible) : pas de
                // ligne. Une reference perimee non plus : le magasin garde la mesure d'avant un changement de memoire pour
                // l'A/B, et l'afficher decrirait une machine qui n'existe plus.
                MemoryMeasuredLabel = LigneMemoire(hw.Memory);
                MemoireSauteeJeu = jeuEnCours && MemoryMeasuredLabel is null;
                ErreurRemesureMemoire = null;
            }
            catch
            {
                // Ce bloc n'avait aucune garde : une panne du cote memoire faisait echouer le
                // scan entier, et le client voyait « echec du scan » pour une mesure de confort.
                // Ne pas savoir combien de canaux sont prouves laisse simplement le compte a zero,
                // c'est-a-dire rien d'affirme, ce que le reste du produit sait deja traiter.
            }
        }
        return st;
    }

    private async Task<(BenchmarkResult? Baseline, bool Degraded)> MesurerEnJeuAsync()
    {
        // Mesure générique en jeu : trouve le jeu lancé (n'importe quel titre, sans liste) et mesure
        // celui-là. Pas de jeu → PID 0 → le runner échoue proprement → scan honnête de la config seule
        // (mode dégradé), jamais un chiffre inventé. Gardé pour la remesure d'Apply, pour que l'avant et
        // l'après portent sur le même jeu.
        _gamePid = _gameLocator.LocateRunningGamePid() ?? 0;
        var jeuReduit = _gamePid == 0 && _gameLocator.JeuOuvert();
        if (jeuReduit) _gamePid = await AttendreLeJeuAsync() ?? 0;
        // Les threads du jeu, observes pendant la meme fenetre que les frames (jamais avant :
        // une consommation prise a un autre moment decrirait un autre moment du jeu).
        var pidObserve = _gamePid;
        var observation = pidObserve > 0 && _observerThreads is { } observer
            ? Task.Run(() => observer(pidObserve, DureeCaptureEnJeu))
            : Task.FromResult<DrDxt.Detection.Cpu.ProcessCpuSample?>(null);
        // Meme fenetre pour la carte reseau : les pertes d'une partie, pas celles d'un bureau au repos.
        var pertes = pidObserve > 0 && _mesurerPertesReseau is { } mesurerPertes
            ? Task.Run(() => mesurerPertes(DureeCaptureEnJeu))
            : Task.FromResult<RepairFinding?>(null);
        // Et pour les pilotes : un pic DPC sous charge est une micro-saccade, au repos il ne prouve rien.
        var bloat = pidObserve > 0 && _mesurerBufferbloat is { } mesurerBloat
            ? Task.Run(() => mesurerBloat(DureeCaptureEnJeu))
            : Task.FromResult<DrDxt.Measurement.Network.VerdictBufferbloat?>(null);
        var exeObserve = pidObserve > 0 ? NomExeDuProcessus(pidObserve) : null;
        // La route vers le serveur que le jeu contacte, pour dire si un relais payant servirait.
        var route = pidObserve > 0 && _mesurerRoute is { } mesurerRoute
            ? mesurerRoute(pidObserve, DureeCaptureEnJeu)
            : Task.FromResult<RepairFinding?>(null);
        var memoireLibre = pidObserve > 0 && _mesurerMemoireLibre is { } mesurerMemoire
            ? mesurerMemoire(DureeCaptureEnJeu)
            : Task.FromResult<ulong?>(null);
        var dpc = pidObserve > 0 && _mesurerDpc is { } mesurerDpc
            ? Task.Run(() => mesurerDpc(DureeCaptureEnJeu))
            : Task.FromResult<RepairFinding?>(null);
        // Ce qui tourne a cote, pendant la meme fenetre. Un scan a deja affiche 164 FPS
        // pendant qu'on regardait l'ecran de DrDXT ; le meme soir, en partie avec nos compilations,
        // 79,5. Le chiffre seul ne veut rien dire : l'ecran dira ce qu'il y avait autour.
        var voisins = Task.Run(() => _voisins.Lire(pidObserve > 0 ? pidObserve : null, CancellationToken.None));
        // Un joueur qui revient sur DrDXT avant la fin réduit le jeu en pleine capture. Les images
        // d'un jeu réduit ne décrivent pas sa partie : le jeu est vérifié chaque seconde, et s'il a quitté
        // l'écran, aucun chiffre n'est donné.
        var surveillance = _gamePid > 0 ? new SurveillanceDuJeu() : null;
        if (surveillance is not null) _ = SurveillerLeJeuAsync(_gamePid, surveillance, DureeCaptureEnJeu);
        var benchResult = await _benchmark.RunAsync(_gamePid, DureeCaptureEnJeu, 5, CancellationToken.None);
        var jeuQuitte = false;
        if (surveillance is not null) { surveillance.Fini = true; jeuQuitte = surveillance.Quitte; }
        DrDxt.Detection.Cpu.ProcessCpuSample? threads;
        try { threads = await observation.ConfigureAwait(true); }
        catch { threads = null; }   // une observation ratee n'est pas « aucune pression »
        _pressionThreads = threads is null
            ? GameThreadPressure.Unknown
            : PressionThreads.Classer(threads.ThreadsChauds.FirstOrDefault()?.PartDUnThreadLogique, threads.ThreadsVus);
        try { _constatPertesReseau = await pertes.ConfigureAwait(true); }
        catch { _constatPertesReseau = null; }   // une mesure ratee n'est pas « aucune perte »
        try
        {
            var verdict = await bloat.ConfigureAwait(true);
            _constatBufferbloat = verdict is null
                ? null
                : DrDxt.Measurement.Network.Bufferbloat.Constat(verdict, DureeCaptureEnJeu);
            RetenirMesureEnvoi(verdict is { Detecte: true } ? verdict.PlafondConseilleBps : 0, exeObserve);
        }
        catch { _constatBufferbloat = null; }
        try { _constatDpc = await dpc.ConfigureAwait(true); }
        catch { _constatDpc = null; }
        try { _constatRoute = await route.ConfigureAwait(true); }
        catch { _constatRoute = null; }
        try
        {
            _constatMemoireEnPartie = DrDxt.Measurement.Memory.MemoireEnPartie.Juger(
                await memoireLibre.ConfigureAwait(true), benchResult.IsOk ? benchResult.Value!.FrameTimesMs : null);
        }
        catch { _constatMemoireEnPartie = null; }
        try { PeuplerLesVoisins(await voisins.ConfigureAwait(true)); }
        catch { PeuplerLesVoisins(null); }
        var degraded = !benchResult.IsOk || jeuQuitte;
        // « tu as quitté ton jeu » peut s'afficher sans aucun jeu ouvert, quand une autre fenêtre a été prise pour le
        // jeu. La phrase nomme le programme retenu : un faux positif se voit tout de suite, pour le joueur comme pour le support.
        if (jeuQuitte)
            DegradedNote = exeObserve is { } retenu
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Scan.Degraded.JeuQuitteNomme"),
                    DureeCaptureEnJeu.TotalSeconds, System.IO.Path.GetFileNameWithoutExtension(retenu))
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _localize("Scan.Degraded.JeuQuitte"), DureeCaptureEnJeu.TotalSeconds);
        else if (degraded)
            // La réparation du service déjà refusée dans la session se dit telle quelle : la phrase générique du service
            // ferait chercher une panne alors qu'il suffit de relancer DrDXT.
            DegradedNote = benchResult.Error is { Code: ErrorCodes.PmonUnavailable } e && e.Message == Docteur.T("SH.DejaRefusee")
                ? e.Message
                : _localize(jeuReduit && _gamePid == 0
                    ? "Scan.Degraded.JeuReduit"
                    : CleDeCouvertureManquante(benchResult.Error!.Value.Code));

        BenchmarkResult? baseline = benchResult.IsOk && !jeuQuitte ? benchResult.Value! with { ProcessusMesure = exeObserve } : null;
        return (baseline, degraded);
    }

    private void PublierLesLibellesDeMesure(BenchmarkResult? baseline)
    {
        // La cadence. Sous vingt echantillons un p95 se confond avec le maximum (1/20 = 5 %) : une capture courte
        // publierait un « ecart de 1 187 ms » juste et vide de sens. On compte les echantillons d'animation, pas les
        // trames : le p95 repose sur cette serie, et mille trames peuvent n'en porter que huit. Compte inconnu : rien.
        CadenceLabel = DrDxt.Measurement.FrametimeStatistics.CadenceP95(baseline) is { } ecart
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.Cadence.Measured"), ecart)
            : null;

        // L'attente d'affichage. Le compte vient de sa serie a elle, publiee par le banc avec le
        // meme filtre que le percentile, des le depart. Compte inconnu ⇒ silence.
        PresentStallLabel = DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline) is { } attente
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.PresentStall.Measured"), attente)
            : null;

        // La saccade. Le p95 des frametimes vient de la meme liste que le compte ci-dessous, avec
        // le meme filtre (`Clean`) : la valeur et la garde ne peuvent pas porter sur deux
        // populations differentes.
        StutterLabel = DrDxt.Measurement.FrametimeStatistics.SaccadeP95(baseline) is { } lentes
            ? string.Format(CultureInfo.CurrentCulture, _localize("Scan.Stutter.Measured"), lentes)
            : null;

        // Le plafond du gain : l'occupation GPU vient de cette mesure, jamais d'une constante ; sans trames ni GPU-busy,
        // pas de ligne. Le rappel de scene reste colle a la borne (seule, elle se lirait comme une promesse) et ne porte
        // aucun chiffre : les series de reference (+5,3 % a +16,3 %) sont celles d'une machine et d'un jeu.
        GpuCeilingLabel = DrDxt.Measurement.PlafondGpu.PhraseDuScan(baseline?.GpuBusyPercent);

        // La courbe de cette mesure et sa médiane.
        CourbeDeLaMesure = CourbeDesTempsDeTrame.Construire(baseline?.FrameTimesMs);
    }

    private void PublierLeResultat(ScanResult scanResult, bool degraded)
    {
        Coverage = scanResult.Coverage;
        // Le rapport de diagnostic decrit le dernier scan. Le garder ici evite de le
        // recalculer au moment de l'export : un rapport qui relance un scan decrirait une autre
        // machine que celle dont le client parle.
        _lastScan = scanResult;
        OnPropertyChanged(nameof(SousTensionPrerequisVisible));
        OnPropertyChanged(nameof(SousTensionPossible));
        // Le registre : on compte les leviers lus et trouves sous-optimaux. Le grand livre
        // est la source, parce qu'il ne contient que les leviers reellement notes, pas les
        // lignes d'information ni les guidages.
        ProvenProblemCount = scanResult.Ledger?.Count(e => e.State == LeverState.Suboptimal) ?? 0;

        foreach (var f in scanResult.Findings)
            Findings.Add(new FindingViewModel(f, _localize));

        PopulateNarrative(degraded);
    }

    private SettingsState PeuplerLesRecommandations(HardwareInventory hw, SettingsState st, BenchmarkResult? baseline, ScanResult scanResult)
    {
        // Consomme la recommandation du profil de la puce : compose le plan issu du profil, remplace
        // et borne les cibles GPU (IGpuTuningTargetSource), et affiche la preuve honnête de chaque
        // choix. Gardé pour qu'Apply exécute ce plan et remesure contre la même base mesurée.
        _scannedBaseline = baseline;
        _goulotScan = scanResult.Bottleneck;
        // Le goulot mesure entre dans l'etat avant la selection : un levier CPU ne
        // s'arme que si le processeur limite ce jeu. Un verdict provisoire (confiance basse)
        // n'arme rien : trop peu de mesure pour justifier une ecriture sur la machine.
        st = st with
        {
            GoulotMesure = scanResult.Bottleneck is { Provisional: false } goulot ? goulot.State : null,
            // Les heures des parties relevées, pour les heures d'activité de Windows Update. Sans relevé
            // branché : null (non lu), jamais « aucune partie ».
            HeuresDeJeu = MemoireParties?.Lire().Select(p => new PlageDeJeu(p.Debut, p.Fin)).ToList(),
        };
        _consumption = _profileConsumer.Consume(hw, st);
        OnPropertyChanged(nameof(ApercuOptimisation));
        Recommendations.Clear();
        Recommendations.CollectionChanged -= NotifierAucuneRecommandation;
        Recommendations.CollectionChanged += NotifierAucuneRecommandation;
        // N'est recommande que ce qui sera reellement applique. Scan reel de la machine
        // de reference : 10 lignes « optimisation recommandee », dont 5 inapplicables selon le
        // catalogue et 2 reglages GPU fermes par decision, qui ne seront jamais ecrits.
        foreach (var item in _consumption.Items.Where(i => i.InExecutedPlan
                     || (i.Applicable && i.Intent == DrDxt.Profiles.ProfileIntent.GpuTune)))
            Recommendations.Add(item);
        return st;
    }

    /// <summary>
    /// « Deja bien reglee » se decide sur les faits de lecture : rien a corriger
    /// (scanner, plan, GPU, BIOS, sante) et rien de non lu. Un audit absent ou un conseiller BIOS en
    /// panne n'ont rien lu : jamais « deja reglee » sur ce qu'on n'a pas vu.
    /// </summary>
    // « Aucun constat » n'est pas « tout lu » : le scanner rend `Unknown` sans constat pour ce qu'il n'a pas lu (XMP,
    // rafraichissement, pagefile, MMCSS), et seule sa couverture le dit. Rien à corriger = aucun constat au-dessus d'Info
    // (un constat Info ne demande rien). Pas de clause « levier sous-optimal au registre » : chaque Suboptimal de
    // LeverCatalog porte deja un constat Minor/Major (un test de mutation la montre redondante).
    private bool DejaReglee(ScanResult scan) =>
        scan.Findings.All(f => f.Severity == Severity.Info)
        && scan.Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 }
        // Un plan GPU ne compte pas : sinon toute machine NVIDIA de bureau (leviers GPU applicables) ne
        // pouvait jamais etre « deja bien reglee ». Un reglage de la carte n'est pas une correction, c'est un essai mesure.
        && _consumption is { Plan.TweakIds.Count: 0 }
        && _biosLu && BiosFindings.Count == 0
        && _derniereSante is { Attention.Count: 0, NonLus.Count: 0 };
}
