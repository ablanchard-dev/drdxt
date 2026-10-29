using System.Globalization;
using System.Text;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Support;

/// <summary>
/// Le rapport que le client colle dans son ticket, pour ne pas déboguer à l'aveugle par mail. Texte brut, pas JSON : le
/// client doit pouvoir le relire avant de l'envoyer, un blob illisible s'envoie sans être lu.
/// Rien de nominatif (ni nom d'utilisateur, ni chemins personnels) : il est fait pour un salon public. Les explications
/// des constats peuvent porter un chemin : les deux appelants (export, paquet d'aide) passent le texte par
/// MachineExportee.AnonymiserTexte, qui les retire.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>Le nom du fichier propose, date comprise : deux rapports d'un meme client se comparent.</summary>
    public static string FileNameFor(DateTimeOffset when) =>
        FormattableString.Invariant($"drdxt-diagnostic-{when:yyyy-MM-dd-HHmm}.txt");

    /// <summary>
    /// Construit le rapport. Pur, a la version du runtime pres : tout arrive deja lu.
    /// </summary>
    /// <param name="scan">Le dernier scan. null = aucun scan encore lance, et ca se dit.</param>
    /// <param name="appVersion">La version du produit : la premiere question de tout support.</param>
    /// <param name="when">L'instant du rapport, injecte (jamais DateTime.Now ici).</param>
    /// <param name="sante">Le tri de l'audit de sante (attention / non lus / verifies), le meme que l'ecran. null = audit
    /// non lance, et ca se dit.</param>
    public static string Build(
        ScanResult? scan, string appVersion, DateTimeOffset when,
        RepairTriage? sante = null, Func<string, string>? localiser = null,
        DrDxt.Measurement.Cpu.CpuDeficitSoutenu? processeur = null,
        DrDxt.Measurement.Network.LigneReseau? reseau = null,
        DrDxt.Measurement.Memory.LigneMemoireMesuree? memoire = null,
        DrDxt.Measurement.LigneDerive? derive = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Docteur.T("Rapport.Titre"));
        Line(sb, "Version", appVersion);
        // Le runtime embarqué ne suit pas les correctifs de la machine ; le support doit le lire.
        Line(sb, "Runtime .NET", Environment.Version.ToString());
        Line(sb, Docteur.T("Rapport.GenereLe"), when.ToString("u", CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (scan is null)
        {
            sb.AppendLine(Docteur.T("Rapport.AucunScan"));
            sb.AppendLine(Docteur.T("Rapport.AucunScan.Conseil"));
            return sb.ToString();
        }

        AppendMachine(sb, scan.Hw);
        AppendBiosLu(sb, scan.State);
        AppendRegister(sb, scan);
        AppendSante(sb, sante);
        AppendConstats(sb, scan, localiser);
        AppendLedger(sb, scan);
        AppendBottleneck(sb, scan);
        AppendCoverage(sb, scan.Hw);
        AppendProcesseurSoutenu(sb, processeur?.PhraseAvec(scan.State?.BiosLu));
        // La ligne mémoire de l'écran, réserve comprise (une référence prise pendant une partie).
        AppendSection(sb, Docteur.T("Rapport.Section.Memoire"), memoire?.Phrase);
        AppendSection(sb, Docteur.T("Rapport.Section.Reseau"), reseau?.Phrase);
        AppendMeasurement(sb, scan.Baseline);
        // La dérive (ce jeu, cette machine, depuis la première mesure) : « mon PC a-t-il ralenti ? ».
        AppendSection(sb, Docteur.T("Rapport.Section.Derive"), derive?.Phrase);
        // La borne du gain de l'écran (domaine Carte graphique), calculée de la mesure du scan.
        AppendSection(sb, Docteur.T("Rapport.Section.Plafond"), DrDxt.Measurement.PlafondGpu.PhraseDuScan(scan.Baseline?.GpuBusyPercent));

        return sb.ToString();
    }

    private static void AppendMachine(StringBuilder sb, HardwareInventory hw)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Machine"));
        // Le nombre de CCD seulement s'il en existe : « 0 CCD » sur un Intel se lirait comme une detection ratee.
        var ccd = hw.Cpu.CcdCount > 0 ? $", {hw.Cpu.CcdCount} CCD" : string.Empty;
        Line(sb, Docteur.T("Rapport.Processeur"), $"{hw.Cpu.Model} ({hw.Cpu.Cores}c/{hw.Cpu.Threads}t{ccd})");
        foreach (var gpu in hw.Gpus)
            Line(sb, Docteur.T("Rapport.Carte"), Docteur.T("Rapport.Carte.Valeur", gpu.Model, gpu.DriverVersion));
        foreach (var stick in hw.Memory)
            // MT/s, pas MHz, comme l'ecran : 3800 MT/s = 1900 MHz reels pour une DDR.
            Line(sb, Docteur.T("Rapport.Memoire"), $"{stick.Manufacturer} {stick.PartNumber}, {stick.ConfiguredMhz} MT/s (nominal {stick.RatedMhz})");
        Line(sb, Docteur.T("Rapport.CarteMere"), $"{hw.Board.Manufacturer} {hw.Board.Product}");
        Line(sb, "BIOS", hw.Bios.Version);
        Line(sb, Docteur.T("Rapport.Chassis"), Texte(hw.Chassis));
        sb.AppendLine();
    }

    // Ce que la NVRAM du BIOS dit, si elle a ete lue ; sinon on le dit : XMP/ReBAR/CSM sont alors des deductions.
    private static void AppendBiosLu(StringBuilder sb, SettingsState? st)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Bios"));
        if (st?.BiosLu is not { Count: > 0 } lus)
        {
            sb.AppendLine(Docteur.T("Rapport.Bios.NonLus"));
        }
        else
        {
            foreach (var l in lus)
                Line(sb, l.Setting, $"{l.Libelle} ({(l.IsDefault ? Docteur.T("Rapport.Bios.Usine") : Docteur.T("Rapport.Bios.Modifie"))})");
        }
        sb.AppendLine();
    }

    private static void AppendRegister(StringBuilder sb, ScanResult scan)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Registre"));
        var problems = scan.Ledger?.Count(e => e.State == LeverState.Suboptimal) ?? 0;
        // Ce compte ne porte que sur les leviers, l'intitulé le dit (un constat « à corriger » peut suivre).
        Line(sb, Docteur.T("Rapport.LeviersACorriger"), problems.ToString(CultureInfo.InvariantCulture));
        Line(sb, Docteur.T("Rapport.LeviersLus"), scan.Coverage is { } c
            ? Docteur.T("Rapport.LeviersLus.Valeur", c.KnownLevers, c.ApplicableLevers, c.UnknownLevers)
            : Docteur.T("Rapport.CouvertureNonCalculee"));
        sb.AppendLine();
    }

    /// <summary>
    /// La sante de la machine, telle que l'ecran l'a triee. La phrase a trois nombres vient de
    /// <see cref="RepairTriage.Phrase"/> : attention, verifie, non lu, jamais l'un sans les autres.
    /// </summary>
    private static void AppendSante(StringBuilder sb, RepairTriage? sante)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Sante"));
        if (sante is null)
        {
            // Un audit non lance n'est pas une machine saine.
            sb.AppendLine(Docteur.T("Rapport.Sante.NonLance"));
            sb.AppendLine();
            return;
        }

        sb.AppendLine(sante.Phrase);
        foreach (var c in sante.Attention)
        {
            Line(sb, $"{RepairLibelles.Ligne(c.Domaine, c.Nom)}",
                (c.ValeurCourante ?? Docteur.T("RF.NonLisible"))
                + (c.ValeurAttendue is { } attendue ? Docteur.T("Rapport.Attendu", attendue) : string.Empty)
                + $"{Docteur.DeuxPoints}{c.Explication}");
            // Le geste sur sa propre ligne, sous le constat, pour se lire sans chercher dans l'explication.
            if (!string.IsNullOrWhiteSpace(c.Remede))
                sb.AppendLine($"    {Docteur.T("Rapport.CommentCorriger")}{Docteur.DeuxPoints}{c.Remede}");
        }
        if (sante.NonLus.Count > 0)
            Line(sb, Docteur.T("Rapport.NonLus"), string.Join(", ", sante.NonLus.Select(c => $"{RepairLibelles.Ligne(c.Domaine, c.Nom)}")));
        // Les états lus sans geste ont ici leur explication sourcée ; l'écran n'en montre que la valeur.
        if (sante.Verifies.Count > 0)
        {
            sb.AppendLine(Docteur.T("Rapport.EtatsLus"));
            foreach (var c in sante.Verifies)
            {
                Line(sb, $"{RepairLibelles.Ligne(c.Domaine, c.Nom)}",
                    (c.ValeurCourante ?? Docteur.T("RF.NonLisible")) + (string.IsNullOrWhiteSpace(c.Explication) ? "" : $"{Docteur.DeuxPoints}{c.Explication}"));
                // Un état lu peut porter ses étapes (le test mémoire jamais lancé) : elles ont leur ligne, comme plus haut.
                if (!string.IsNullOrWhiteSpace(c.Remede))
                    sb.AppendLine($"    {Docteur.T("Rapport.CommentCorriger")}{Docteur.DeuxPoints}{c.Remede}");
            }
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Les constats du scanner, avec leur texte : le détail des leviers n'en porte qu'une partie. Sans traducteur, la
    /// section se tait plutôt que d'écrire des clés.
    /// </summary>
    private static void AppendConstats(StringBuilder sb, ScanResult scan, Func<string, string>? localiser)
    {
        if (localiser is null || scan.Findings.Count == 0) return;
        sb.AppendLine(Docteur.T("Rapport.Section.Constats"));
        foreach (var f in scan.Findings.OrderByDescending(f => f.Severity))
        {
            var gravite = Docteur.T(f.Severity switch
            {
                Severity.Major => "Rapport.Gravite.Important",
                Severity.Minor => "Rapport.Gravite.ACorriger",
                _ => "Rapport.Gravite.ASavoir",
            });
            var domaine = f.Category switch
            {
                "Gpu" => Docteur.T("Rapport.Carte"),
                "Cpu" => Docteur.T("Rapport.Processeur"),
                "Memory" => Docteur.T("Rapport.Memoire"),
                "Latency" => Docteur.T("Rapport.Latence"),
                "Network" => Docteur.T("Rapport.Reseau"),
                "Display" => Docteur.T("Rapport.Ecran"),
                "Storage" => Docteur.T("Rapport.Stockage"),
                _ => f.Category,
            };
            Line(sb, $"{domaine} · {gravite}", localiser(f.TitleKey));
        }
        sb.AppendLine();
    }

    private static void AppendLedger(StringBuilder sb, ScanResult scan)
    {
        if (scan.Ledger is not { Count: > 0 }) return;

        sb.AppendLine(Docteur.T("Rapport.Section.Leviers"));
        foreach (var entry in scan.Ledger)
            // L'identifiant, pas le libelle traduit : il se cherche dans le code.
            Line(sb, entry.LeverId, Texte(entry.State));
        sb.AppendLine();
    }

    private static void AppendBottleneck(StringBuilder sb, ScanResult scan)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Limite"));
        if (scan.Bottleneck is not { } verdict)
        {
            sb.AppendLine(Docteur.T("Rapport.NonClasseParentheses"));
            sb.AppendLine();
            return;
        }

        Line(sb, Docteur.T("Rapport.Etat"), Texte(verdict.State));

        // Sans classification, la confiance ne qualifie rien (« grande confiance dans un etat inconnu »).
        if (verdict.State != BottleneckState.UnknownPartial)
        {
            Line(sb, Docteur.T("Rapport.Confiance"), Texte(verdict.Confidence));
        }

        Line(sb, Docteur.T("Rapport.Provisoire"), Docteur.T(verdict.Provisional ? "Rapport.Oui" : "Rapport.Non"));
        // Le motif en clair, pour comprendre le verdict sans rejouer le scan.
        Line(sb, Docteur.T("Rapport.Motif"), MotifDuGoulot.Dire(verdict));
        sb.AppendLine();
    }

    private static void AppendCoverage(StringBuilder sb, HardwareInventory hw)
    {
        var rows = GpuTuningCoverage.For(hw);
        if (rows.Count == 0) return;

        // Le réglage est ouvert machine par machine : ce qui le ferme est dit, sans rien promettre.
        sb.AppendLine(Docteur.T("Rapport.Section.ReglageCarte"));
        sb.AppendLine(Docteur.T("Rapport.ReglageCarte.Regle"));
        foreach (var row in rows)
            Line(sb, row.Model,
                Docteur.T("Rapport.ReglageCarte.Ligne", Texte(row.Support), Texte(row.Architecture),
                    // Le levier de la carte écrit un décalage d'horloge, jamais une tension : aucune op de tension de carte
                    // n'existe (NoVoltageWriteOpExistsGateTests), quelle que soit l'architecture ou sa validation.
                    Docteur.T("Rapport.VoltageJamaisEcrit")));
        sb.AppendLine();
    }

    /// <summary>Ce que le processeur rend sous charge longue, qui explique un jeu limite par le processeur. Rien sans
    /// mesure.</summary>
    private static void AppendProcesseurSoutenu(StringBuilder sb, string? phrase)
        => AppendSection(sb, Docteur.T("Rapport.Section.ProcesseurSoutenu"), phrase);

    /// <summary>Une section de l'écran, sa phrase sous son titre.</summary>
    private static void AppendSection(StringBuilder sb, string titre, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return;
        sb.AppendLine(titre);
        sb.AppendLine(phrase);
        sb.AppendLine();
    }

    private static void AppendMeasurement(StringBuilder sb, BenchmarkResult? baseline)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Mesure"));
        if (baseline is null)
        {
            // Une mesure absente n'est pas une mesure a zero. PresentMon n'est pas le seul instrument (repli DXGI).
            sb.AppendLine(Docteur.T("Rapport.AucuneMesure"));
            sb.AppendLine();
            return;
        }

        // Qui a compte. Le repli DXGI ne voit ni la latence ni le focus : le rapport le dit.
        Line(sb, Docteur.T("Rapport.Instrument"), baseline.Instrument is "DXGI" ? Docteur.T("Rapport.Instrument.Dxgi") : "PresentMon");
        Line(sb, Docteur.T("Rapport.Processus"), baseline.ProcessusMesure ?? Docteur.T("Rapport.Inconnu"));
        Line(sb, Docteur.T("Rapport.FpsMoyen"), baseline.AvgFps.ToString("F1", CultureInfo.InvariantCulture));
        Line(sb, "1% low", baseline.P1LowFps.ToString("F1", CultureInfo.InvariantCulture));
        // Les lows ont leur propre marge, plus large que celle de la moyenne.
        Line(sb, Docteur.T("Rapport.Marge1Low"), double.IsFinite(baseline.P1LowMarginOfError)
            ? baseline.P1LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : Docteur.T("Rapport.MargeInconnue"));
        // Sous ImagesPourLe01Low images, le 0,1 % le plus lent n'est qu'une image, la pire : il ne se chiffre pas.
        if (baseline.FrameTimesMs.Count < ImagesPourLe01Low)
            Line(sb, "0.1% low", Docteur.T("Rapport.P01Insuffisant", baseline.FrameTimesMs.Count, ImagesPourLe01Low));
        else
        {
            Line(sb, "0.1% low", baseline.P01LowFps.ToString("F1", CultureInfo.InvariantCulture));
            Line(sb, Docteur.T("Rapport.Marge01Low"), double.IsFinite(baseline.P01LowMarginOfError)
                ? baseline.P01LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
                : Docteur.T("Rapport.MargeInconnue"));
        }
        // La marge et le nombre de passes sans lesquels un chiffre de FPS ne veut rien dire.
        // +inf sous trois passes, que la culture invariante ecrit « Infinity ».
        Line(sb, Docteur.T("Rapport.Marge"), double.IsFinite(baseline.MarginOfError)
            ? baseline.MarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : Docteur.T("Rapport.MargeInconnue"));
        Line(sb, Docteur.T("Rapport.Passes"), baseline.Runs.ToString(CultureInfo.InvariantCulture));
        Line(sb, Docteur.T("Rapport.Frames"), baseline.FrameTimesMs.Count.ToString(CultureInfo.InvariantCulture));
        // Les queues de l'écran (domaine Latence) : une moyenne cache la saccade que le joueur ressent.
        LigneQueue(sb, Docteur.T("Rapport.TramesLentes"), DrDxt.Measurement.FrametimeStatistics.SaccadeP95(baseline));
        LigneQueue(sb, Docteur.T("Rapport.EcartAnimation"), DrDxt.Measurement.FrametimeStatistics.CadenceP95(baseline));
        LigneQueue(sb, Docteur.T("Rapport.AttenteAffichage"), DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline));
        sb.AppendLine();
    }

    private static void LigneQueue(StringBuilder sb, string label, double? ms)
    {
        if (ms is { } v) Line(sb, label, v.ToString("F2", CultureInfo.InvariantCulture) + " ms");
    }

    // Un intitulé plus long que la colonne (« Long Duration Power Limit (PL1) ») garde son espace avant les deux-points.
    private static void Line(StringBuilder sb, string label, string value) =>
        sb.AppendLine(FormattableString.Invariant($"{label,-21} : {value}"));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Les valeurs affichees, dans la langue du docteur : jamais un nom de symbole C# (« UnknownPartial », « AdaOrOlder »)
    // dans un texte que le client colle en public. Le `_ =>` rend le nom brut plutot que de mentir, et
    // `No_raw_identifier_reaches_the_client` fera rougir toute valeur ajoutee.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string Texte(ChassisType chassis) => chassis switch
    {
        ChassisType.Desktop => Docteur.T("Rapport.Chassis.Fixe"),
        ChassisType.Laptop => Docteur.T("Rapport.Chassis.Portable"),
        ChassisType.Handheld => Docteur.T("Rapport.Chassis.Console"),
        ChassisType.Unknown => Docteur.T("Rapport.Chassis.Inconnu"),
        _ => chassis.ToString(),
    };

    private static string Texte(LeverState state) => state switch
    {
        LeverState.Optimal => Docteur.T("Rapport.Levier.Optimal"),
        LeverState.Suboptimal => Docteur.T("Rapport.Gravite.ACorriger"),
        LeverState.Unknown => Docteur.T("RF.Statut.NonLu"),
        LeverState.NotApplicable => Docteur.T("Rapport.Levier.SansObjet"),
        _ => state.ToString(),
    };

    /// <summary>Le 0,1 % le plus lent d'une mesure n'est plus une seule image qu'à partir de mille.</summary>
    public const int ImagesPourLe01Low = 1000;

    private static string Texte(BottleneckState state) => MotifDuGoulot.Libelle(state);

    private static string Texte(BottleneckConfidence confidence) => confidence switch
    {
        BottleneckConfidence.Low => Docteur.T("Rapport.Confiance.Faible"),
        BottleneckConfidence.Medium => Docteur.T("Rapport.Confiance.Moyenne"),
        BottleneckConfidence.High => Docteur.T("Rapport.Confiance.Elevee"),
        _ => confidence.ToString(),
    };

    private static string Texte(GpuTuningSupport support) => support switch
    {
        GpuTuningSupport.Validated => Docteur.T("Rapport.Support.Valide"),
        GpuTuningSupport.ReadOnly => Docteur.T("Rapport.Support.Lecture"),
        GpuTuningSupport.NotApplicable => Docteur.T("Rapport.Support.Rien"),
        _ => support.ToString(),
    };

    private static string Texte(GpuArchitecture architecture) => architecture switch
    {
        GpuArchitecture.Unknown => Docteur.T("Rapport.Archi.Inconnue"),
        GpuArchitecture.AdaOrOlder => Docteur.T("Rapport.Archi.Ada"),
        GpuArchitecture.Blackwell => "NVIDIA Blackwell (RTX 50)",
        GpuArchitecture.Rdna3 => "AMD RDNA3",
        GpuArchitecture.Rdna2 => "AMD RDNA2",
        GpuArchitecture.Rdna4 => "AMD RDNA4",
        GpuArchitecture.Rdna1 => "AMD RDNA1",
        GpuArchitecture.Gcn => "AMD GCN",
        _ => architecture.ToString(),
    };
}
