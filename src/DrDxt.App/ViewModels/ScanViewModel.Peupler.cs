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

        // La latence vraie quand le tCL est lisible dans la référence du kit. C'est la seule
        // grandeur comparable entre deux kits : DDR5-6000 CL30 et DDR4-3200 CL16 valent tous
        // deux 10,00 ns malgré 88 % d'écart sur l'étiquette.
        // Le tCL vient du kit, pas de la machine : aucune API Windows n'expose les timings
        // effectifs. D'où « kit CL18 », qui dit d'où sort le chiffre au lieu de le présenter
        // comme une mesure. Pas de tCL lisible ⇒ on n'affiche rien, jamais une latence devinée.
        int? cl = DramPartNumber.TryParseCasLatency(modules[0].PartNumber);
        string latency = cl is null
            ? string.Empty
            : F($" · CAS {MemoryMath.TrueLatencyNs(cl.Value, speed):0.0} ns (kit CL{cl})");

        // Le kit peut être noté plus haut que la vitesse réellement entraînée (un kit 4000 qui
        // tourne à 3800). Ce n'est pas un défaut (c'est fréquent en dual rank, le contrôleur
        // mémoire ne tient pas toujours le nominal), donc ce n'est pas un Finding : le canal des
        // findings est celui des problèmes à corriger. Ici c'est une information, dans le même
        // idiome que « (kit CL18) » : dire d'où sort le chiffre.
        // Le nominal vient du part number, pas de WMI : Win32_PhysicalMemory.Speed s'effondre
        // sur la valeur entraînée (mesuré : un kit F4-4000C18 y rend 3800, pas 4000).
        // RatedMhz = 0 signifie « jamais lu » ⇒ on ne compare pas contre un zéro.
        int rated = modules.Max(m => m.RatedMhz);
        string ratedNote = rated > speed ? $" (kit {rated})" : string.Empty;

        // MT/s, pas MHz : 3800 MT/s = 1900 MHz réels. L'étiquette précédente était fausse.
        // « CAS » nomme la latence du kit (à ne pas confondre avec le temps d'accès
        // mesuré) ; espace insécable devant chaque « · » : jamais un séparateur seul en fin de ligne.
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

    /// <summary>Pourquoi la perf en jeu manque, en distinguant ce que l'utilisateur peut y faire
    /// de ce qu'il ne peut pas.
    ///
    /// « Lance une partie, puis relance le scan » ne vaut que s'il n'y avait aucun jeu a mesurer
    /// (`PID_INVALID`). Si le service de mesure ne repond pas (`PMON_UNAVAIL`, par exemple un conflit
    /// de tube avec le service Intel), relancer n'y change rien : le joueur lirait le meme message a
    /// l'infini. Un diagnostic qui accuse l'utilisateur de son propre defaut est pire qu'un silence.</summary>
    private static string CleDeCouvertureManquante(string codeDErreur) => codeDErreur switch
    {
        ErrorCodes.PmonUnavailable => "Scan.Degraded.ServiceUnavailable",
        // Aucun jeu en cours, ou un jeu dont on ne peut pas lire les frames : la seule famille de
        // cas ou lancer une partie change vraiment quelque chose.
        _ => "Scan.Degraded.PresentMon",
    };

    /// <summary>Depose la mesure du jour dans le journal, pour que le scan suivant puisse
    /// conclure a une derive.
    ///
    /// Ne fait jamais echouer le scan. Le journal est un confort de diagnostic :
    /// punir l'utilisateur d'un fichier qui ne s'ecrit pas serait le meme defaut que la mesure
    /// memoire avait, et qu'on a corrige. Une mesure absente (mode degrade) n'est pas journalisee
    /// non plus : un journal qui accumule des trous ne compare plus rien.</summary>
    private void JournaliserLaMesure(BenchmarkResult? mesure)
    {
        if (_performanceJournal is null || _machineId is null || mesure is null || !(mesure.AvgFps > 0))
            return;

        try
        {
            // Le jeu voyage avec la mesure. Sans lui, deux titres differents se comparaient et le
            // produit annonçait une degradation qui n'etait qu'un changement de jeu. Non identifie
            // => `null`, et le juge se taira plutot que de rapprocher n'importe quoi.
            // Et la scene declaree : un menu ne se compare pas a une partie.
            _performanceJournal.Ajouter(new PerformanceSample(
                DateTimeOffset.UtcNow, _machineId(), mesure.AvgFps,
                mesure.MarginOfError, mesure.Runs, mesure.HygieneKeptFraction,
                _gameLocator.NameOfGame(_gamePid), SceneDeclaree.Valeur));
        }
        catch
        {
            // Le magasin enveloppe deja ses frontieres ; cette garde couvre le reste (une
            // serialisation inattendue, un identifiant machine qui leve). Un scan ne tombe pas
            // pour un journal.
        }

        DireLaDerive();
    }

    /// <summary>
    /// « Mon PC est-il plus lent qu'avant ? » : la question que le journal savait deja trancher.
    ///
    /// `PerformanceJournal.Derive` existait, teste, et n'etait appele que par des tests : le
    /// produit ecrivait un journal que personne ne relisait. Meme defaut que l'audit qui prenait
    /// une photo puis l'oubliait, mais du cote performance, celui qui interesse un joueur, parce
    /// qu'une degradation lente ne se voit dans aucun scan isole.
    ///
    /// Tout le jugement reste dans le juge : deux machines ne se comparent jamais, la date
    /// ordonne, deux expositions differentes ne se comparent pas, et un ecart sous la resolution
    /// n'est pas une alerte. Ici on ne fait que choisir la phrase : le moteur rend des nombres,
    /// l'affichage parle, et il parle dans la langue du client.
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

            // La date de l'etalon, pas de la plus vieille entree : quand une premiere mesure trop
            // bruitee est ecartee, l'ecran doit dater la mesure qui sert vraiment.
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

    /// <summary>
    /// Le résumé compte ce que la carte compte. Il ne voyait que le Majeur, pendant que
    /// la carte marquait « À corriger » tout ce qui dépasse Info (santé et BIOS compris) : sur un
    /// scan réel, « Aucun problème critique détecté » à côté de deux domaines « À corriger ».
    /// </summary>
    private string ResumeDesProblemes()
    {
        var (critical, aCorriger) = CompteCommeLaCarte();

        // Chaque nombre choisit sa forme. « 6 point(s) à corriger, dont 1 critique(s) »,
        // c'est l'interface qui n'a pas pris la peine de compter, dans la phrase qu'on lit en premier.
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

    // Le seul compte des critiques et des points à corriger. Une liste « Résumé du
    // diagnostic » recomptait de son côté, sans le BIOS ni les réparations, et cochait « Aucun
    // problème critique » sous « dont 1 critique(s) ». Corrigée, puis retirée : elle redisait
    // l'en-tête. Un deuxième endroit qui compte finira par compter autrement.
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

        // Le sous-titre reprend la note deja choisie : deux endroits qui reformulent la meme cause
        // finissent toujours par se contredire, et c'est ce qui vient d'arriver ici.
        SubtitleText = degraded
            ? DegradedNote ?? _localize("Scan.Degraded.PresentMon")
            : _localize("UI.Result.Subtitle");

        // Passe par VerdictText et non par le récit brut : le résumé lu ne peut pas affirmer un
        // verdict de santé que le titre vient de retenir faute de couverture.
        AssistantSummary = ResumeDuRapport();
    }

    // « Des points à reprendre. 12 points à corriger » redirait deux fois la même chose, sous un titre
    // qui la dit déjà. Quand le compte des points est la phrase, le verdict se tait ; une lecture incomplète
    // ou une machine saine gardent le leur, c'est lui qui porte l'information.
    private string ResumeDuRapport() =>
        Majuscule(VerdictText is var v && (v == _localize("UI.Verdict.Problems") || v == _localize("UI.Verdict.SanteACorriger"))
            ? ProblemSummary
            : $"{VerdictText}. {ProblemSummary}");

    // Affiche les constats du docteur BIOS, erreurs de réglage d'abord. Le conseiller ne fait que
    // lire et comparer l'inventaire et les réglages déjà produits par ce scan (il ne redétecte
    // rien, n'écrit rien). Ordre : les constats à guider avant ceux d'information ou illisibles,
    // et dans chaque groupe la sévérité la plus haute d'abord (XMP/EXPO coupé, Major, passe donc
    // premier). Une clé secondaire stable garde l'ordre des prérequis du conseiller (Above-4G →
    // CSM → ReBAR se lit de haut en bas). Au mieux : un Result en échec ou une panne de frontière
    // laisse la section vide et ne casse jamais le scan gratuit. Rend les constats bruts du
    // conseiller (ni filtrés ni triés) pour nourrir le générateur de guide avec la même liste ;
    // le générateur garde lui-même la part de classe Guide. null si rien n'a été conseillé ou en
    // cas de panne.
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
            // La section BIOS s'ajoute au scan ; une panne de frontière donne une section vide au lieu
            // de casser le scan (gratuit).
            BiosFindings.Clear();
            raw = null;
            _biosLu = false;
        }

        OnPropertyChanged(nameof(HasBiosFindings));
        return raw;
    }

    /// <summary>
    /// L'audit de sante arrive a l'ecran.
    ///
    /// Le produit n'affiche que la pile `Attention` et le decompte des deux autres. Montrer la
    /// cinquantaine de constats bruts serait exact et illisible ; n'afficher que `Attention` sans
    /// dire combien d'etats n'ont pas pu etre lus ferait passer nos angles morts pour un bilan
    /// de sante. <see cref="RepairTriage.Phrase"/> porte les trois nombres ensemble, et c'est
    /// pour ca qu'elle est prise telle quelle plutot que recomposee ici.
    /// </summary>
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
            // Hors du thread UI : le service lance `bcdedit` (jusqu'a 5 s) et amorce des compteurs
            // de performance (~300 ms chacun). Meme motif que le compte de canaux memoire.
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
            // Sans point d'avant le souci (pas de geste), une seconde phrase, sans « pour revenir ».
            // La liste des points illisible pendant un souci (le docteur ne rend ce constat qu'avec un souci en
            // cours) : le point de l'Optimiser le contiendra quand même, c'est dit.
            // « un souci est en cours » sans dire lequel ; le souci daté (Provenance)
            // est nommé. {0} = le souci ; {1} = le point sain.
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

            // Les angles morts arrivent a l'ecran, dans leur propre liste : les melanger avec ce
            // qui merite correction ferait passer un trou de lecture pour un defaut.
            foreach (var c in triage.NonLus)
                RepairBlindSpots.Add(new RepairFindingViewModel(c));

            // Le titre compte tous les points, cette phrase seulement l'audit de santé ;
            // dit tel quel, « 13 » en haut et « 12 » en bas ne se contredisent plus.
            RepairSummary = PhraseDeLaSante(triage);

            // Le pilier maintenir : la photo du jour est enregistree, et ce qui a change depuis la
            // precedente arrive a l'ecran. Une panne de ce cote ne coute que cette section.
            try
            {
                // Ecrire la photo du jour et la comparer a la precedente prend 390 a
                // 400 ms de disque (mesure, sonde de gel + jalons). Sur le fil d'interface, c'etait
                // le moment ou le scanner s'arretait de bouger. Le resultat revient ici pour l'ecran.
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
                // Meme regle que le `Fail` du magasin, et c'est ici que la section survit au reste
                // de l'audit : une panne de ce cote ne se raconte pas comme « rien n'a change ».
                RepairChanges.Clear();
                RepairChangesWarning = Docteur.T("App.HistoriqueNonLu");
            }
        }
        catch
        {
            // L'audit s'ajoute au scan : une panne de frontiere (WMI intermittent, journal
            // illisible) degrade la section a rien plutot que de casser le scan. Et on ne laisse
            // Aucune phrase derriere : un resume rescape d'une collecte en panne mentirait sur ce
            // qui a ete regarde.
            RepairFindings.Clear();
            RepairBlindSpots.Clear();
            RepairChanges.Clear();
            RepairChangesWarning = null;
            RepairSummary = null;
            // Une santé non lue ne laisse pas dire « tout lu, rien à corriger » : la lecture est incomplète. Le triage posé
            // avant la panne tombe aussi : DejaReglee le lit, et rendait « déjà bien réglée » sur une lecture interrompue.
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

    // Construit le guide BIOS personnalisé (le « comment » exact) à partir des mêmes constats
    // bruts du conseiller (le générateur filtre lui-même la classe Guide). Texte de guidage
    // seul : jamais une écriture, jamais dans le lot appliqué. Affiché seulement si le générateur
    // a produit au moins une étape à faire : un guide qui n'a que le secours ne guide rien et
    // reste null, et la section ne s'affiche jamais sur une machine bien réglée. Au mieux : un
    // Result en échec ou une panne de frontière laisse le guide null et ne casse jamais le scan
    // gratuit.
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

            // La couture « emmene-moi dans le BIOS ». Elle pose le marqueur avant de
            // demander le redemarrage : c'est ce qui permet de prouver au retour que le
            // changement a pris. Les constats et l'instantane des grandeurs sont ceux de ce
            // scan, captures ici et pas relus au moment du clic : l'utilisateur doit repartir
            // avec l'etat qu'il a sous les yeux.
            //
            // Sans couture (null), le VM ne montre pas le bouton et l'instruction manuelle
            // (« appuie sur Suppr au demarrage ») reste affichee de toute facon.
            BiosGuide = _firmwareTrip is null
                ? new BiosGuideViewModel(guide, _localize)
                : new BiosGuideViewModel(guide, _localize,
                    async delay =>
                    {
                        // La mesure d'avant se prend ici, au depart, pour que l'A/B du retour
                        // compare a la machine d'aujourd'hui et pas a une reference d'un autre jour. Un
                        // echec ne bloque pas le voyage : l'A/B dira seulement qu'il n'a pas de mesure
                        // d'avant fraiche.
                        // Jeu lancé, pas de mesure : une mesure d'avant faussée à la baisse ferait annoncer un
                        // gain au retour. Sans elle, l'A/B dit honnêtement qu'il n'a pas de mesure fraîche.
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
            // Le guide est un renfort qui s'ajoute aux constats ; une panne de frontière donne « pas de
            // guide » au lieu de casser le scan (gratuit).
            BiosGuide = null;
        }
    }

    // Le verdict du retour. Le verifieur et son marqueur existaient deja et
    // n'etaient resolus par personne : « on prouve que ca a pris » n'avait jamais tourne en
    // production. Ici il tourne, contre l'etat frais relu par ce scan.
    //
    // Meilleur effort, comme le reste de la section BIOS : un marqueur corrompu ou une panne
    // d'E/S laisse la section vide plutot que de casser le scan (gratuit). Ce qu'on ne fait
    // Pas, c'est fabriquer un verdict : pas de marqueur = pas de section, jamais un « tout va
    // bien » sur un voyage qui n'a pas eu lieu.
    /// <summary>Ce que l'A/B memoire a donne, pour que l'appelant sache s'il a le droit de
    /// remplacer la reference derriere.</summary>
    private enum MemoryAbOutcome { Aucun, Conclu, Refusee }

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

        // L'A/B memoire ne se declenche que au retour d'un vrai voyage BIOS. C'est la seule
        // fois ou remesurer se justifie : quelque chose a pu changer, et une mesure coute vingt a
        // trente secondes, jusqu'a trois fois si la machine s'agite.
        if (!verdict.HadPendingChange || _memoryBaseline is null || _machineId is null)
            return MemoryAbOutcome.Aucun;

        try
        {
            // Sur un fil de travail, comme le bloc de rafraichissement. Le banc parcourt 384 Mo
            // et reessaie jusqu'a trois fois : appele ici sans `Task.Run`, il gelait la fenetre
            // jusqu'a une minute et demie, sur le chemin meme qui doit montrer la preuve au client.
            var jeuEnCours = _gameLocator.JeuOuvert();
            var ab = await Task.Run(
                () => _memoryBaseline.CompareAfterChange(_machineId(), hw.Memory, verdict.DepartUtc, jeuEnCours));

            MemoryAb = MemoryAbViewModel.From(ab, _localize);
            return ab is { HasVerdict: false } ? MemoryAbOutcome.Refusee : MemoryAbOutcome.Conclu;
        }
        catch
        {
            // Une panne du cote memoire ne jette pas le verdict BIOS deja obtenu : le client
            // perdrait la seule chose qui lui dit si son changement a pris.
            MemoryAb = null;
            return MemoryAbOutcome.Aucun;
        }
    }
}
