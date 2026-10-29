using static DrDxt.Core.TexteFrancais;
using System.Collections.ObjectModel;
using System.Globalization;
using DrDxt.App.Profiles;
using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.App.ViewModels;

/// <summary>Remplit l'écran après le scan : composants, mesure en jeu, résumé, constats du BIOS et audit de santé.</summary>
public sealed partial class ScanViewModel
{
    /// <summary>Le build et l'UBR de Windows (« 26200.6899 »), lus sans droits ; <c>null</c> = non lu.</summary>
    internal static string? BuildDeWindows()
    {
        try
        {
            using var cle = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return cle?.GetValue("CurrentBuild") is string b && cle.GetValue("UBR") is int ubr ? $"{b}.{ubr}" : null;
        }
        catch (Exception) { return null; }
    }

    private void ApplyBaseline(BenchmarkResult? baseline)
    {
        if (baseline is null)
        {
            AvgFpsDisplay = Dash;
            AvgFpsAvecMarge = Dash;
            P1LowDisplay = Dash;
            P1LowAvecMarge = Dash;
            MarginDisplay = Dash;
            return;
        }

        AvgFpsDisplay = ((int)Math.Round(baseline.AvgFps)).ToString(CultureInfo.InvariantCulture);
        P1LowDisplay = ((int)Math.Round(baseline.P1LowFps)).ToString(CultureInfo.InvariantCulture);
        // Sous trois passes la marge vaut +inf : sans ce test, l'ecran ecrirait « ± Infinity ».
        MarginDisplay = double.IsFinite(baseline.MarginOfError)
            ? $"± {baseline.MarginOfError.ToString("0.#", CultureInfo.InvariantCulture)}"
            : _localize("Dashboard.Marge.Inconnue");
        // La marge collee au chiffre : l'ecran ne peut plus montrer l'un sans l'autre.
        AvgFpsAvecMarge = $"{AvgFpsDisplay} {MarginDisplay}";
        P1LowAvecMarge = $"{P1LowDisplay} " + (double.IsFinite(baseline.P1LowMarginOfError)
            ? $"± {baseline.P1LowMarginOfError.ToString("0.#", CultureInfo.InvariantCulture)}"
            : _localize("Dashboard.Marge.Inconnue"));
    }

    /// <summary>« Intel(R) Core(TM) i7 » tel que le firmware l'écrit ; les marques
    /// déposées n'apprennent rien au joueur.</summary>
    internal static string? NomLisible(string? nom) => nom is null ? null
        : System.Text.RegularExpressions.Regex.Replace(nom, @"\((R|TM|tm|r)\)", "").Replace("  ", " ").Trim();

    private void PopulateComponents(HardwareInventory? hw)
    {
        Components.Clear();

        Components.Add(new ComponentItem("Cpu", _localize("UI.Component.Cpu"),
            Text(NomLisible(hw?.Cpu.Model))));

        Components.Add(new ComponentItem("Gpu", _localize("UI.Component.Gpu"),
            Text(hw is { Gpus.Count: > 0 } ? (CartesGraphiques.CarteDediee(hw) ?? hw.Gpus[0]).Model : null)));

        Components.Add(new ComponentItem("Ram", _localize("UI.Component.Ram"),
            DescribeMemory(hw)));

        Components.Add(new ComponentItem("Ssd", _localize("UI.Component.Storage"),
            _stockageLu));

        Components.Add(new ComponentItem("Windows", _localize("UI.Component.Windows"),
            DescribeWindows()));
        // Latence, réseau et BIOS rejoignent le cercle. Le BIOS dit la carte mère et sa version, lues ici ;
        // latence et réseau disent ce que DrDXT y mesure.
        _composantsSansPiece =
        [
            new ComponentItem("Latency", _localize("Carte.Domaine.latence"), _localize("UI.Component.Latency.Valeur")),
            new ComponentItem("Firmware", _localize("Carte.Domaine.bios"), DescribeBios(hw)),
            new ComponentItem("Network", _localize("Carte.Domaine.reseau"), _localize("UI.Component.Network.Valeur")),
        ];
        AnnoncerSatellites();
    }

    internal static string DescribeBios(HardwareInventory? hw)
    {
        var carte = hw?.Board.Product is { Length: > 0 } p ? p.Trim() : null;
        var version = hw?.Bios.Version is { Length: > 0 } v ? "BIOS " + v.Trim() : null;
        return Text(string.Join(" · ", new[] { carte, version }.Where(x => x is not null)) is { Length: > 0 } t ? t : null);
    }

    private static string DescribeMemory(HardwareInventory? hw)
    {
        if (hw is not { Memory.Count: > 0 })
            return Dash;

        var modules = hw.Memory;
        int speed = modules.Max(m => m.ConfiguredMhz);
        // Les tailles de mémoire ne sont pas dans le contrat : on donne honnêtement le nombre de modules et la vitesse configurée.
        int count = modules.Count;
        string slots = count == 1 ? "1 module" : $"{count} modules";
        if (speed <= 0) return slots;

        // La latence vraie, quand le tCL est lisible dans la référence du kit : la seule grandeur comparable entre kits
        // (DDR5-6000 CL30 et DDR4-3200 CL16 valent 10,00 ns malgré 88 % d'écart d'étiquette). Le tCL vient du kit, aucune
        // API Windows n'expose les timings effectifs : d'où « kit CL18 ». Pas de tCL lisible ⇒ rien, jamais deviné.
        int? cl = DramPartNumber.TryParseCasLatency(modules[0].PartNumber);
        string latency = cl is null
            ? string.Empty
            : F($" · CAS {MemoryMath.TrueLatencyNs(cl.Value, speed):0.0} ns (kit CL{cl})");

        // Un kit noté plus haut que sa vitesse entraînée (4000 qui tourne à 3800, fréquent en dual rank) n'est pas un
        // défaut, donc pas un Finding : une information, comme « (kit CL18) ». Le nominal vient du part number, pas de
        // WMI (Win32_PhysicalMemory.Speed rend la valeur entraînée : mesuré, F4-4000C18 → 3800). RatedMhz = 0 = jamais lu.
        int rated = modules.Max(m => m.RatedMhz);
        string ratedNote = rated > speed ? $" (kit {rated})" : string.Empty;

        // MT/s, pas MHz : 3800 MT/s = 1900 MHz réels. « CAS » nomme la latence du kit, pas le temps d'accès mesuré ;
        // espace insécable devant chaque « · » : jamais un séparateur seul en fin de ligne.
        return $"{slots} · {speed} MT/s{ratedNote}{latency}";
    }

    /// <summary>Le disque principal tel que lu par <see cref="LireStockageAsync"/> ; un tiret tant qu'il ne l'est pas.</summary>
    private string _stockageLu = Dash;

    /// <summary>Lit le disque principal hors du fil de l'écran : WMI (MSFT_PhysicalDisk, puis Win32_DiskDrive, avec reprises)
    /// peut prendre plusieurs secondes sur un service froid, et la fenêtre gelait pendant ce temps.</summary>
    private async Task LireStockageAsync()
    {
        try { _stockageLu = await Task.Run(DescribeStorage).ConfigureAwait(true); }
        catch (Exception) { _stockageLu = Dash; }
    }

    private string DescribeStorage()
    {
        var result = _storage.Detect();
        if (!result.IsOk || result.Value is not { Count: > 0 } devices)
            return Dash;

        var primary = devices[0];
        var model = string.IsNullOrWhiteSpace(primary.Model) ? Dash : primary.Model.Trim();
        var kind = string.IsNullOrWhiteSpace(primary.MediaKind) ? string.Empty : primary.MediaKind.Trim();
        // On retire un type de support inconnu ou vide plutôt que d'afficher un vilain suffixe « · Unknown ».
        var kindKnown = kind.Length > 0 && !kind.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        return kindKnown ? $"{model} · {kind}" : model;
    }

    private static string DescribeWindows()
    {
        var v = Environment.OSVersion.Version;
        // Windows 11 annonce un build de noyau 22000 ou plus. On le distingue pour un libellé honnête.
        string name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} ({v.Build})";
    }

    /// <summary>Pourquoi la perf en jeu manque, en distinguant ce que l'utilisateur peut y faire de ce qu'il ne peut pas.
    /// « Lance une partie, puis relance le scan » ne vaut que sans jeu a mesurer (`PID_INVALID`) ; si le service de mesure
    /// ne repond pas (`PMON_UNAVAIL`, par exemple un conflit de tube avec le service Intel), relancer n'y change rien.</summary>
    private static string CleDeCouvertureManquante(string codeDErreur) => codeDErreur switch
    {
        ErrorCodes.PmonUnavailable => "Scan.Degraded.ServiceUnavailable",
        // Aucun jeu, ou un jeu dont on ne lit pas les frames : le seul cas ou lancer une partie change quelque chose.
        _ => "Scan.Degraded.PresentMon",
    };

    /// <summary>Depose la mesure du jour dans le journal, pour que le scan suivant puisse conclure a une derive. Ne fait
    /// jamais echouer le scan (un confort de diagnostic). Une mesure absente (mode degrade) n'est pas journalisee : un
    /// journal a trous ne compare plus rien.</summary>
    private void JournaliserLaMesure(BenchmarkResult? mesure)
    {
        if (_performanceJournal is null || _machineId is null || mesure is null || !(mesure.AvgFps > 0))
            return;

        try
        {
            // Le jeu voyage avec la mesure : sinon un changement de jeu passerait pour une degradation. Non identifie
            // => `null`, et le juge se tait. Et la scene declaree : un menu ne se compare pas a une partie.
            _performanceJournal.Ajouter(new PerformanceSample(
                DateTimeOffset.UtcNow, _machineId(), mesure.AvgFps,
                mesure.MarginOfError, mesure.Runs, mesure.HygieneKeptFraction,
                _gameLocator.NameOfGame(_gamePid), SceneDeclaree.Valeur));
        }
        catch
        {
            // Le magasin enveloppe ses frontieres ; cette garde couvre le reste (serialisation inattendue, identifiant
            // machine qui leve). Un scan ne tombe pas pour un journal.
        }

        DireLaDerive();
    }

    /// <summary>
    /// « Mon PC est-il plus lent qu'avant ? », tranche par `PerformanceJournal.Derive` : une degradation lente ne se voit
    /// dans aucun scan isole. Le jugement reste dans le juge (deux machines ou deux expositions ne se comparent pas, la
    /// date ordonne, un ecart sous la resolution n'est pas une alerte) ; ici on choisit seulement la phrase.
    /// </summary>
    private void DireLaDerive()
    {
        PerformanceDriftLabel = null;
        if (_performanceJournal is null) return;

        try
        {
            var journal = _performanceJournal.Lire();
            var preuve = PerformanceJournal.Derive(journal).Game?.Preuve;
            // Rien a comparer, ou une comparaison que le juge a refusee : la ligne n'existe pas.
            // « Pas comparable » ne se raconte pas comme « stable ».
            if (preuve is not { } p || p.Outcome == EvidenceOutcome.Invalid) return;

            // La date de l'etalon, pas de la plus vieille entree : une premiere mesure trop bruitee peut etre ecartee.
            var depuis = (PerformanceJournal.Etalon(journal)?.TakenUtc ?? journal.Min(e => e.TakenUtc))
                .ToLocalTime().DateTime;
            PerformanceDriftLabel = p.Outcome switch
            {
                EvidenceOutcome.ProvenRegression => string.Format(CultureInfo.CurrentCulture,
                    _localize("Scan.Drift.Regression"), depuis, Math.Abs(p.DeltaPct), p.ResolutionPct),
                EvidenceOutcome.ProvenGain => string.Format(CultureInfo.CurrentCulture,
                    _localize("Scan.Drift.Gain"), depuis, p.DeltaPct, p.ResolutionPct),
                _ => string.Format(CultureInfo.CurrentCulture,
                    _localize("Scan.Drift.None"), depuis, p.ResolutionPct),
            };
        }
        catch
        {
            // Un historique illisible ne fait pas echouer un scan, et ne se raconte pas non plus.
            PerformanceDriftLabel = null;
        }
    }

    /// <summary>Le résumé compte ce que la carte compte : tout ce qui dépasse Info (santé et BIOS compris), sinon
    /// « Aucun problème critique détecté » s'affiche à côté de domaines « À corriger ».</summary>
    private string ResumeDesProblemes()
    {
        var (critical, aCorriger) = CompteCommeLaCarte();

        // Chaque nombre choisit sa forme : jamais « 6 point(s) à corriger, dont 1 critique(s) ».
        if (critical > 0)
        {
            // Les deux nombres : le total que la carte marque, et combien sont critiques.
            var cle = aCorriger == 1 ? "UI.Result.ACorrigerDontCritiques.Seul"
                : critical == 1 ? "UI.Result.ACorrigerDontCritiques.Un"
                : "UI.Result.ACorrigerDontCritiques";
            var modele = _localize(cle);
            return modele.Contains("{1}", StringComparison.Ordinal)
                ? string.Format(CultureInfo.CurrentCulture, modele, aCorriger, critical)
                : Format(modele, aCorriger);
        }
        return aCorriger == 0
            ? _localize("UI.Result.NoCritical")
            : Format(_localize(aCorriger == 1 ? "UI.Result.ACorrigerSansCritique.Un" : "UI.Result.ACorrigerSansCritique"), aCorriger);
    }

    // Le seul compte des critiques et des points à corriger : un deuxième endroit qui compte finit par compter
    // autrement (« Aucun problème critique » sous « dont 1 critique(s) »).
    private (int Critiques, int ACorriger) CompteCommeLaCarte()
    {
        // Le Majeur se compte partout où la carte le montre : un XMP coupé (BIOS, le plus gros gain
        // réel) ne doit pas finir en « aucun critique ».
        int critical = ScoreNarrative.CriticalCount(Findings)
                       + RepairFindings.Count(r => r.Severity == Severity.Major)
                       + BiosFindings.Count(b => b.Severity == Severity.Major);
        int aCorriger = Findings.Count(f => f.Severity > Severity.Info)
                        + RepairFindings.Count(r => r.Severity > Severity.Info)
                        + BiosFindings.Count(b => b.Severity > Severity.Info);
        return (critical, aCorriger);
    }

    /// <summary>De quand datent les points affichés. Une couture d'horloge pour les tests.</summary>
    internal Func<DateTime> Maintenant { get; set; } = () => DateTime.Now;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _dateDuScan;

    private void PopulateNarrative(bool degraded)
    {
        ProblemSummary = ResumeDesProblemes();
        // La langue du docteur, pas le format régional de Windows : celui-ci peut porter un autre calendrier.
        DateDuScan = string.Create(Docteur.Langue, $"{_localize("Scan.Date")} {Maintenant():dd'/'MM · HH':'mm}");
        SignalerReposCarte();
        OnPropertyChanged(nameof(ExpertVisible));
        OnPropertyChanged(nameof(LabelExpertAccord));
        OnPropertyChanged(nameof(ExpertVentilateurVisible));

        // Le sous-titre reprend la note deja choisie : deux reformulations de la meme cause finissent par se contredire.
        SubtitleText = degraded
            ? DegradedNote ?? _localize("Scan.Degraded.PresentMon")
            : _localize("UI.Result.Subtitle");

        // Passe par VerdictText et non par le récit brut : le résumé lu ne peut pas affirmer un
        // verdict de santé que le titre vient de retenir faute de couverture.
        AssistantSummary = ResumeDuRapport();
    }

    // Quand le compte des points est la phrase, le verdict se tait (« Des points à reprendre. 12 points à corriger »
    // redirait le titre) ; une lecture incomplète ou une machine saine gardent le leur, qui porte l'information.
    private string ResumeDuRapport() =>
        Majuscule(VerdictText is var v && (v == _localize("UI.Verdict.Problems") || v == _localize("UI.Verdict.SanteACorriger"))
            ? ProblemSummary
            : $"{VerdictText}. {ProblemSummary}");

    // Les constats du docteur BIOS, qui compare l'inventaire et les réglages de ce scan sans rien redétecter ni écrire.
    // Ordre : à guider d'abord, puis sévérité décroissante (XMP/EXPO coupé, Major, en tête), puis l'ordre des prérequis
    // du conseiller (Above-4G → CSM → ReBAR). Une panne laisse la section vide sans casser le scan gratuit. Rend les
    // constats bruts (ni filtrés ni triés) pour le générateur de guide, qui garde lui-même la classe Guide ; null si
    // rien n'a été conseillé ou en cas de panne.
    private IReadOnlyList<BiosFinding>? PopulateBiosFindings(HardwareInventory hw, SettingsState st)
    {
        BiosFindings.Clear();
        _biosLu = false;
        IReadOnlyList<BiosFinding>? raw = null;
        try
        {
            var advised = _biosAdvisor.Advise(hw, st);
            _biosLu = advised.IsOk;
            if (!advised.IsOk || advised.Value is not { Count: > 0 } conseils)
                return null;

            // Le lien PCIe lu remplace le constat « non lu » : on ne dit plus qu'on ne sait pas ce qu'on sait.
            try { _lienPcie = _lirePcie?.Invoke(); } catch { _lienPcie = null; }
            IReadOnlyList<BiosFinding> findings = _lienPcie is null
                ? conseils
                : conseils.Where(f => f.Kind != FirmwareSettingKind.PcieGen).ToList();

            raw = findings;

            var ordered = findings
                .Select((f, i) => (f, i))
                .OrderBy(x => x.f.MatrixClass == BiosMatrixClass.Guide ? 0 : 1)
                .ThenByDescending(x => (int)x.f.Severity)
                .ThenBy(x => x.i)
                .Select(x => x.f);

            // XMP coupé est aussi un constat du scan : si la mesure y a retiré la promesse d'images, le BIOS le dit pareil.
            var motifXmp = Findings.FirstOrDefault(x => x.Cle == "Finding.Memory.XmpOff")?.PromesseRetiree;
            foreach (var f in ordered)
                BiosFindings.Add(new BiosFindingViewModel(f, _localize, f.Kind == FirmwareSettingKind.XmpExpo ? motifXmp : null));
        }
        catch
        {
            // Une panne de frontière donne une section vide, jamais un scan cassé.
            BiosFindings.Clear();
            raw = null;
            _biosLu = false;
        }

        OnPropertyChanged(nameof(HasBiosFindings));
        return raw;
    }

    /// <summary>L'audit de sante a l'ecran : la pile `Attention` et le decompte des deux autres (la cinquantaine de
    /// constats bruts serait illisible ; taire les etats non lus ferait passer nos angles morts pour un bilan).
    /// <see cref="RepairTriage.Phrase"/> porte les trois nombres ensemble : prise telle quelle.</summary>
    internal static string PhraseDeLaSante(RepairTriage triage) => Docteur.T("App.AuditSeul", triage.Phrase);

    // Vrai quand l'audit de santé a levé : la section est vide parce qu'elle n'a pas été lue, pas parce qu'elle est saine.
    private bool _santeNonLue;

    private async Task PopulateRepairAuditAsync()
    {
        _santeNonLue = false;
        RepairFindings.Clear();
        RepairBlindSpots.Clear();
        RepairChanges.Clear();
        RepairChangesWarning = null;
        RepairSummary = null;
        AvertissementPoint = null;
        Bilan = BilanSante.Construire([]);
        // Le triage du scan precedent ne parle pas pour celui-ci (DejaReglee le lit).
        _derniereSante = null;
        _constatsLus = null;

        if (_repairAudit is null)
        {
            OnPropertyChanged(nameof(HasRepairFindings));
            OnPropertyChanged(nameof(HasRepairBlindSpots));
            OnPropertyChanged(nameof(PeutRelancerEnAdministrateur));
            return;
        }

        try
        {
            // Hors du thread UI : `bcdedit` (jusqu'a 5 s) et l'amorce des compteurs de performance (~300 ms chacun).
            var audit = _repairAudit;
            var constats = await Task.Run(() => audit());
            // Le constat reseau mesure pendant la partie rejoint la sante de la machine.
            if (_constatPertesReseau is { } pertesReseau)
                constats = constats.Append(pertesReseau).ToList();
            // Les gains que DrDXT avait poses et qui ont ete defaits depuis.
            if (_journalLeviers is { } journalLeviers && _registreGains is { } registre)
            {
                constats = constats.Concat(journalLeviers.Constats(registre)).ToList();
                // Encore dans la liste signée, plus dans le journal : le service pourrait le reposer, et l'écran ne le montrait pas.
                if (await RelireOrphelinsDuManifesteAsync() is { Count: > 0 } orphelins)
                    constats = constats.Append(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.ConstatDesOrphelins(orphelins)).ToList();
                // Windows mis à jour depuis le dernier scan : le bilan de ce que DrDXT avait posé, relu sans rien reposer.
                if (BuildDeWindows() is { } build
                    && journalLeviers.ApresMiseAJour(registre, build, DrDxt.Core.DossierDonnees.Chemin("build-windows-vu.json")) is { } apres)
                    constats = constats.Append(apres).ToList();
            }
            // Les reglages de latence du jeu (Reflex, generation d'images), lus, jamais ecrits.
            if (_lireReglagesJeu is { } lireJeu)
            {
                // Un fichier par jeu joué, hors du fil de l'écran.
                try { constats = constats.Concat(await Task.Run(lireJeu)).ToList(); }
                catch { /* fichier du jeu illisible : rien d'affirme */ }
            }
            if (_constatTestMemoire is { } testMemoire)
                constats = constats.Append(testMemoire).ToList();
            // Un test d'une session précédente resté sans fin : la machine s'est-elle arrêtée pendant ?
            else if (!TestMemoireEnCours && JugerRepriseTestMemoire is { } reprise)
            {
                try { if (await Task.Run(reprise) is { } r) constats = constats.Append(r).ToList(); }
                catch { /* journaux illisibles : rien d'affirme */ }
            }
            if (_constatCadence is { } cadence)
                constats = constats.Append(cadence).ToList();
            if (_constatBufferbloat is { } bufferbloat)
                constats = constats.Append(bufferbloat).ToList();
            if (_constatDpc is { } latenceDpc)
                constats = constats.Append(latenceDpc).ToList();
            if (_constatRoute is { } route)
                constats = constats.Append(route).ToList();
            if (_constatMemoireEnPartie is { } memoireEnPartie)
                constats = constats.Append(memoireEnPartie).ToList();
            if (_verificationPilote is { } verification)
            {
                try { if (await verification.ConfigureAwait(true) is { } pilote) constats = constats.Append(pilote).ToList(); }
                catch { /* source injoignable : rien n'est affirmé */ }
            }
            // Sans point d'avant le souci (pas de geste), une seconde phrase, sans « pour revenir ». Liste des points
            // illisible pendant un souci (ce constat n'existe qu'avec un souci en cours) : le point de l'Optimiser le
            // contiendra quand même, c'est dit. Le souci daté (Provenance) est nommé. {0} = le souci ; {1} = le point sain.
            if (constats.FirstOrDefault(c => c.Nom == "point de restauration sain") is { } point)
            {
                // L'avertissement commence par le souci : sa première lettre prend la majuscule.
                var souci = point.Provenance is { Length: > 0 } p ? char.ToUpper(p[0], CultureInfo.CurrentUICulture) + p[1..] : "";
                AvertissementPoint = point.ValeurCourante is not { } sain
                    ? (point.Statut == RepairStatus.Unknown
                        ? string.Format(CultureInfo.CurrentUICulture, _localize("Apply.RestorePoint.DuringIssueUnread"), souci) : null)
                    : string.Format(CultureInfo.CurrentUICulture, _localize(point.Action == RepairAction.Aucune
                        ? "Apply.RestorePoint.DuringIssueNoPoint" : "Apply.RestorePoint.DuringIssue"), souci, sain);
            }
            Bilan = BilanSante.Construire(constats);
            _constatsLus = constats;
            var triage = RepairTriage.Trier(constats);
            _derniereSante = triage;
            ArmerBcd(triage);
            ArmerReparation(triage);

            foreach (var c in triage.Attention)
                RepairFindings.Add(new RepairFindingViewModel(c));

            // Les angles morts dans leur propre liste : melanges aux corrections, un trou de lecture passerait pour un defaut.
            foreach (var c in triage.NonLus)
                RepairBlindSpots.Add(new RepairFindingViewModel(c));

            // Le titre compte tous les points, cette phrase seulement l'audit de santé, et le dit (« 13 » en haut,
            // « 12 » en bas).
            RepairSummary = PhraseDeLaSante(triage);

            // Le pilier maintenir : la photo du jour est enregistree, et ce qui a change depuis la precedente arrive a
            // l'ecran. Une panne de ce cote ne coute que cette section.
            try
            {
                // Hors du fil d'interface : ecrire et comparer la photo prend 390 a 400 ms de disque (mesure, sonde de
                // gel + jalons), pendant lesquels le scanner s'arretait de bouger.
                var journalSante = _journalSante;
                var journalLu = journalSante is null ? null : await Task.Run(() => journalSante(constats));
                if (journalLu is { } journal)
                {
                    RepairChangesWarning = journal.Avertissement;
                    foreach (var d in journal.Nouvelles)
                        RepairChanges.Add(LigneDeChangement(d));
                }
            }
            catch
            {
                // Meme regle que le `Fail` du magasin : une panne ne se raconte pas comme « rien n'a change ».
                RepairChanges.Clear();
                RepairChangesWarning = Docteur.T("App.HistoriqueNonLu");
            }
        }
        catch
        {
            // Une panne de frontiere (WMI intermittent, journal illisible) vide la section sans casser le scan, et ne
            // laisse aucune phrase : un resume rescape mentirait sur ce qui a ete regarde.
            RepairFindings.Clear();
            RepairBlindSpots.Clear();
            RepairChanges.Clear();
            RepairChangesWarning = null;
            RepairSummary = null;
            // Une santé non lue ne laisse pas dire « tout lu, rien à corriger ». Le triage posé avant la panne tombe aussi :
            // DejaReglee le lit, et dirait « déjà bien réglée » sur une lecture interrompue.
            _santeNonLue = true;
            _derniereSante = null;
            Bilan = BilanSante.Construire([]);
            OnPropertyChanged(nameof(VerdictText));
        }

        OnPropertyChanged(nameof(HasRepairFindings));
        OnPropertyChanged(nameof(HasRepairBlindSpots));
        OnPropertyChanged(nameof(PeutRelancerEnAdministrateur));
        OnPropertyChanged(nameof(HasRepairChanges));
    }

    // Le guide BIOS personnalisé (le « comment » exact), à partir des mêmes constats bruts du conseiller. Texte seul :
    // jamais une écriture, jamais dans le lot appliqué. Null sans au moins une étape à faire (un guide qui n'a que le
    // secours ne guide rien), donc absent d'une machine bien réglée. Une panne laisse le guide null sans casser le scan.
    private void PopulateBiosGuide(HardwareInventory hw, IReadOnlyList<BiosFinding>? findings, SettingsState st)
    {
        BiosGuide = null;
        if (findings is not { Count: > 0 })
            return;

        try
        {
            var result = _guideGenerator.Generate(hw, findings);
            if (!result.IsOk || result.Value is not { Steps.Count: > 0 } guide)
                return;

            // « Emmene-moi dans le BIOS » pose le marqueur avant le redemarrage, pour prouver au retour que le changement a
            // pris. Constats et grandeurs captures ici, pas au clic : l'etat que l'utilisateur a sous les yeux. Sans
            // couture (null), pas de bouton ; l'instruction manuelle (« appuie sur Suppr au demarrage ») reste affichee.
            BiosGuide = _firmwareTrip is null
                ? new BiosGuideViewModel(guide, _localize)
                : new BiosGuideViewModel(guide, _localize,
                    async delay =>
                    {
                        // La mesure d'avant se prend au depart : l'A/B du retour compare a la machine d'aujourd'hui. Un
                        // echec ne bloque pas le voyage (l'A/B dira qu'il n'a pas de mesure fraiche). Jeu lancé, pas de
                        // mesure : faussée à la baisse, elle ferait annoncer un gain au retour.
                        if (_memoryBaseline is not null && _machineId is not null
                            && !_gameLocator.JeuOuvert())
                        {
                            try { await Task.Run(() => _memoryBaseline.MesurerMaintenant(_machineId(), hw.Memory)); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { }
                        }
                        return await _firmwareTrip.ArmAndRebootAsync(findings, st, delay, CancellationToken.None);
                    },
                    () => _firmwareTrip.AnnulerAsync(CancellationToken.None));
        }
        catch
        {
            // Une panne de frontière donne « pas de guide », jamais un scan cassé.
            BiosGuide = null;
        }
    }

    /// <summary>Ce que l'A/B memoire a donne, pour que l'appelant sache s'il a le droit de
    /// remplacer la reference derriere.</summary>
    private enum MemoryAbOutcome { Aucun, Conclu, Refusee }

    // Le verdict du retour (« on prouve que ca a pris »), contre l'etat frais relu par ce scan. Meilleur effort : un
    // marqueur corrompu ou une panne d'E/S laisse la section vide sans casser le scan (gratuit). Pas de marqueur = pas
    // de section, jamais un « tout va bien » sur un voyage qui n'a pas eu lieu.
    private async Task<MemoryAbOutcome> PopulateBiosLoopAsync(SettingsState st, HardwareInventory hw)
    {
        BiosLoop = null;
        if (_firmwareTrip is null)
            return MemoryAbOutcome.Aucun;

        BiosLoopResult? verdict = null;
        try
        {
            var verified = _firmwareTrip.VerifyOnReturn(st);
            if (!verified.IsOk || verified.Value is null)
                return MemoryAbOutcome.Aucun;

            verdict = verified.Value;
            BiosLoop = BiosLoopViewModel.From(verdict, _localize);
        }
        catch
        {
            BiosLoop = null;
            return MemoryAbOutcome.Aucun;
        }

        // L'A/B memoire seulement au retour d'un vrai voyage BIOS : quelque chose a pu changer, et une mesure coute 20 a
        // 30 s, jusqu'a trois fois si la machine s'agite.
        if (!verdict.HadPendingChange || _memoryBaseline is null || _machineId is null)
            return MemoryAbOutcome.Aucun;

        try
        {
            // Sur un fil de travail : le banc parcourt 384 Mo et reessaie jusqu'a trois fois, soit jusqu'a une minute et
            // demie de fenetre gelee sur le fil d'interface.
            var jeuEnCours = _gameLocator.JeuOuvert();
            var ab = await Task.Run(
                () => _memoryBaseline.CompareAfterChange(_machineId(), hw.Memory, verdict.DepartUtc, jeuEnCours));

            MemoryAb = MemoryAbViewModel.From(ab, _localize);
            return ab is { HasVerdict: false } ? MemoryAbOutcome.Refusee : MemoryAbOutcome.Conclu;
        }
        catch
        {
            // Une panne du cote memoire ne jette pas le verdict BIOS deja obtenu.
            MemoryAb = null;
            return MemoryAbOutcome.Aucun;
        }
    }
}
