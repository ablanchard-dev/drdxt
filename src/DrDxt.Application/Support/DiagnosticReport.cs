using System.Globalization;
using System.Text;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Support;

/// <summary>
/// Le rapport que le client colle dans son ticket.
///
/// Sans lui, le premier client difficile consomme une semaine : on débogue à l'aveugle par mail,
/// une question à la fois, avec quelqu'un qui ne sait pas où regarder. Un dev solo qui vend à des
/// joueurs un produit qui les envoie dans leur BIOS ne peut pas se permettre ça.
///
/// Le format est du texte brut, pas du JSON : le client le colle dans un Discord ou un mail, et
/// il doit pouvoir le relire lui-même avant de l'envoyer. Un blob illisible ne se relit pas, donc
/// il s'envoie sans être lu, et c'est comme ça qu'on publie ce qu'on ne voulait pas publier.
///
/// ══ ce qui n'entre jamais dans ce fichier ══
///
/// Rien de nominatif : ni nom d'utilisateur, ni chemins personnels. Ce rapport est destiné à être collé
/// dans un salon public. Ce qu'on met est ce
/// dont le support a besoin pour comprendre une machine, et rien de plus.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>Le nom du fichier propose. Date dans le nom : deux rapports d'un meme client a
    /// deux moments differents racontent une histoire que le second seul ne raconte pas.</summary>
    public static string FileNameFor(DateTimeOffset when) =>
        FormattableString.Invariant($"drdxt-diagnostic-{when:yyyy-MM-dd-HHmm}.txt");

    /// <summary>
    /// Construit le rapport. Pur : meme entree, meme texte ; aucune lecture de machine ici, tout
    /// arrive deja lu, ce qui le rend entierement testable.
    /// </summary>
    /// <param name="scan">Le dernier scan. null = aucun scan encore lance, et ca se dit.</param>
    /// <param name="appVersion">La version du produit : la premiere question de tout support.</param>
    /// <param name="when">L'instant du rapport, injecte (jamais DateTime.Now ici).</param>
    /// <param name="sante">Le tri de l'audit de sante (attention / non
    /// lus / verifies), le meme que l'ecran. null = audit non lance, et ca se dit.</param>
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
        // Le rapport reprend telle quelle la ligne mémoire de l'écran : débit et latence mesurés, et la réserve
        // d'une référence prise pendant une partie.
        AppendSection(sb, Docteur.T("Rapport.Section.Memoire"), memoire?.Phrase);
        AppendSection(sb, Docteur.T("Rapport.Section.Reseau"), reseau?.Phrase);
        AppendMeasurement(sb, scan.Baseline);
        // La dérive (ce jeu, cette machine, depuis la première mesure) : à l'écran, absente d'ici. C'est
        // pourtant la réponse à « mon PC a-t-il ralenti ? », la première question d'un joueur au support.
        AppendSection(sb, Docteur.T("Rapport.Section.Derive"), derive?.Phrase);
        // La borne du gain, affichée à l'écran (domaine Carte graphique), absente du rapport. Elle se
        // calcule de la mesure du scan, déjà ici : la même phrase, sans rien recevoir de l'appelant.
        AppendSection(sb, Docteur.T("Rapport.Section.Plafond"), DrDxt.Measurement.PlafondGpu.PhraseDuScan(scan.Baseline?.GpuBusyPercent));

        return sb.ToString();
    }

    private static void AppendMachine(StringBuilder sb, HardwareInventory hw)
    {
        sb.AppendLine(Docteur.T("Rapport.Section.Machine"));
        // Le nombre de CCD n'est imprime que s'il en existe. Vu sur un vrai rapport de cette
        // machine : « 20c/28t, 0 CCD » sur un Intel, ou la notion n'existe pas. Un champ sans
        // objet affiche a zero se lit comme une detection ratee, et fait perdre une minute au
        // support sur un probleme qui n'existe pas.
        var ccd = hw.Cpu.CcdCount > 0 ? $", {hw.Cpu.CcdCount} CCD" : string.Empty;
        Line(sb, Docteur.T("Rapport.Processeur"), $"{hw.Cpu.Model} ({hw.Cpu.Cores}c/{hw.Cpu.Threads}t{ccd})");
        foreach (var gpu in hw.Gpus)
            Line(sb, Docteur.T("Rapport.Carte"), Docteur.T("Rapport.Carte.Valeur", gpu.Model, gpu.DriverVersion));
        foreach (var stick in hw.Memory)
            // MT/s, pas MHz. `ScanViewModel` porte deja cette correction : 3800 MT/s = 1900 MHz
            // reels, et l'etiquette MHz est fausse pour une DDR. Le rapport suit la meme regle que
            // l'ecran : c'est lui que le client colle dans son ticket, donc lui qui voyage.
            Line(sb, Docteur.T("Rapport.Memoire"), $"{stick.Manufacturer} {stick.PartNumber}, {stick.ConfiguredMhz} MT/s (nominal {stick.RatedMhz})");
        Line(sb, Docteur.T("Rapport.CarteMere"), $"{hw.Board.Manufacturer} {hw.Board.Product}");
        Line(sb, "BIOS", hw.Bios.Version);
        Line(sb, Docteur.T("Rapport.Chassis"), Texte(hw.Chassis));
        sb.AppendLine();
    }

    // Ce que la NVRAM du BIOS dit, quand elle a pu etre lue. Sinon on le dit : les
    // etats XMP/ReBAR/CSM du rapport sont alors des deductions, pas des lectures.
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
        // « Problèmes prouvés : 0 » suivi d'un constat « à corriger » se contredisait à
        // la lecture ; ce compte ne porte que sur les leviers, l'intitulé le dit.
        Line(sb, Docteur.T("Rapport.LeviersACorriger"), problems.ToString(CultureInfo.InvariantCulture));
        Line(sb, Docteur.T("Rapport.LeviersLus"), scan.Coverage is { } c
            ? Docteur.T("Rapport.LeviersLus.Valeur", c.KnownLevers, c.ApplicableLevers, c.UnknownLevers)
            : Docteur.T("Rapport.CouvertureNonCalculee"));
        sb.AppendLine();
    }

    /// <summary>
    /// La sante de la machine, telle que l'ecran l'a triee.
    ///
    /// Ce que l'ecran montre (arrets brutaux, memoire jamais testee…) doit aussi arriver dans le
    /// document que le client garde. La phrase a trois nombres vient de
    /// <see cref="RepairTriage.Phrase"/> : ce qui merite l'attention, ce qui est verifie, ce qui n'a
    /// pas pu etre lu, jamais l'un sans les autres.
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
        // Les états lus sans geste portent aussi une explication sourcée (fréquence
        // effective, bridage, incidents noyau, MMCSS…). L'écran n'en montre que la valeur ; ici,
        // dans le document que le client garde, ils ont leur phrase.
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
    /// Les constats du scanner, avec leur texte. Relu sur un rapport réel : le constat
    /// principal de la machine de référence (le pilote graphique en performances maximales pour tout
    /// le PC) n'y figurait pas ; seul « vrr : à corriger » passait, par le détail des leviers. Sans
    /// traducteur (appels de test historiques), la section se tait plutôt que d'écrire des clés.
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
            // L'identifiant du levier et son etat, pas le libelle traduit : le support lit ce
            // fichier, et un identifiant se cherche dans le code alors qu'une phrase non.
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

        // Une confiance ne s'affiche pas sans la chose qu'elle qualifie. Sinon on imprimerait
        // « Confiance : High » sous « Etat : UnknownPartial », ce qui se lit
        // « grande confiance dans un etat inconnu ». La confiance qualifie une classification ;
        // quand il n'y a pas de classification, elle ne qualifie rien.
        if (verdict.State != BottleneckState.UnknownPartial)
        {
            Line(sb, Docteur.T("Rapport.Confiance"), Texte(verdict.Confidence));
        }

        Line(sb, Docteur.T("Rapport.Provisoire"), Docteur.T(verdict.Provisional ? "Rapport.Oui" : "Rapport.Non"));
        // Le motif en clair : c'est lui qui permet au support de comprendre le verdict sans
        // rejouer le scan. Un etat sans son motif est un code, pas un diagnostic.
        Line(sb, Docteur.T("Rapport.Motif"), MotifDuGoulot.Dire(verdict));
        sb.AppendLine();
    }

    private static void AppendCoverage(StringBuilder sb, HardwareInventory hw)
    {
        var rows = GpuTuningCoverage.For(hw);
        if (rows.Count == 0) return;

        // La section annonçait « réglable » sans dire à quelles conditions. Le réglage
        // est ouvert machine par machine ; ce qui le ferme est dit, sans rien promettre.
        sb.AppendLine(Docteur.T("Rapport.Section.ReglageCarte"));
        sb.AppendLine(Docteur.T("Rapport.ReglageCarte.Regle"));
        foreach (var row in rows)
            Line(sb, row.Model,
                Docteur.T("Rapport.ReglageCarte.Ligne", Texte(row.Support), Texte(row.Architecture),
                    Docteur.T(row.VoltageTuning ? "Rapport.Oui" : "Rapport.Non")));
        sb.AppendLine();
    }

    /// <summary>Ce que le processeur rend sous charge longue. Affiche a l'ecran, absent du
    /// rapport jusqu'ici (relu dans le rapport reel) : c'est pourtant ce qui explique un jeu limite par le
    /// processeur. Rien n'est ecrit quand la mesure n'existe pas.</summary>
    private static void AppendProcesseurSoutenu(StringBuilder sb, string? phrase)
        => AppendSection(sb, Docteur.T("Rapport.Section.ProcesseurSoutenu"), phrase);

    /// <summary>La ligne réseau de l'écran, jusque-là absente du rapport.</summary>
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
            // Une mesure absente n'est pas une mesure a zero. Le dire evite au support de
            // chercher pourquoi les chiffres sont mauvais alors qu'il n'y en a pas.
            // PresentMon n'est plus le seul instrument (repli DXGI) ; ne pas l'accuser seul.
            sb.AppendLine(Docteur.T("Rapport.AucuneMesure"));
            sb.AppendLine();
            return;
        }

        // Qui a compte. Le repli DXGI ne voit ni la latence ni le focus : le rapport le dit.
        Line(sb, Docteur.T("Rapport.Instrument"), baseline.Instrument is "DXGI" ? Docteur.T("Rapport.Instrument.Dxgi") : "PresentMon");
        Line(sb, Docteur.T("Rapport.Processus"), baseline.ProcessusMesure ?? Docteur.T("Rapport.Inconnu"));
        Line(sb, Docteur.T("Rapport.FpsMoyen"), baseline.AvgFps.ToString("F1", CultureInfo.InvariantCulture));
        Line(sb, "1% low", baseline.P1LowFps.ToString("F1", CultureInfo.InvariantCulture));
        // Les lows ont leur propre marge (plus large que celle de la moyenne) ; sans elle, le chiffre
        // passerait pour aussi solide que le FPS moyen.
        Line(sb, Docteur.T("Rapport.Marge1Low"), double.IsFinite(baseline.P1LowMarginOfError)
            ? baseline.P1LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : Docteur.T("Rapport.MargeInconnue"));
        Line(sb, "0.1% low", baseline.P01LowFps.ToString("F1", CultureInfo.InvariantCulture));
        Line(sb, Docteur.T("Rapport.Marge01Low"), double.IsFinite(baseline.P01LowMarginOfError)
            ? baseline.P01LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : Docteur.T("Rapport.MargeInconnue"));
        // La marge et le nombre de passes sans lesquels un chiffre de FPS ne veut rien dire.
        // +inf sous trois passes, que la culture invariante ecrit « Infinity ».
        Line(sb, Docteur.T("Rapport.Marge"), double.IsFinite(baseline.MarginOfError)
            ? baseline.MarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : Docteur.T("Rapport.MargeInconnue"));
        Line(sb, Docteur.T("Rapport.Passes"), baseline.Runs.ToString(CultureInfo.InvariantCulture));
        Line(sb, Docteur.T("Rapport.Frames"), baseline.FrameTimesMs.Count.ToString(CultureInfo.InvariantCulture));
        // Les queues que l'écran affiche (domaine Latence), absentes jusqu'ici. Une moyenne cache la
        // saccade ; le support qui ne voit que le FPS moyen ne voit pas ce que le joueur ressent.
        LigneQueue(sb, Docteur.T("Rapport.TramesLentes"), DrDxt.Measurement.FrametimeStatistics.SaccadeP95(baseline));
        LigneQueue(sb, Docteur.T("Rapport.EcartAnimation"), DrDxt.Measurement.FrametimeStatistics.CadenceP95(baseline));
        LigneQueue(sb, Docteur.T("Rapport.AttenteAffichage"), DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline));
        sb.AppendLine();
    }

    private static void LigneQueue(StringBuilder sb, string label, double? ms)
    {
        if (ms is { } v) Line(sb, label, v.ToString("F2", CultureInfo.InvariantCulture) + " ms");
    }

    // « {label,-22}: » collait les deux-points aux intitulés plus longs que la
    // colonne (« Long Duration Power Limit (PL1): Auto »). Même sortie pour les intitulés courts.
    private static void Line(StringBuilder sb, string label, string value) =>
        sb.AppendLine(FormattableString.Invariant($"{label,-21} : {value}"));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Les valeurs affichees, dans la langue du docteur
    //
    // Mesure du 05/09/2026, en usage reel sur une vraie machine : ce rapport imprimait
    // « Chassis : Desktop », « Etat : UnknownPartial », « Confiance : High »,
    // « Validated / AdaOrOlder ». Ce sont des noms de symboles C#, dans un texte que le client lit
    // et colle dans un salon public. Un identifiant brut n'est pas une information : c'est du code
    // qui a fuit jusqu'a l'utilisateur, et ca demande au client de deviner ce que le programme
    // voulait dire.
    //
    // Le `_ =>` rend le nom brut plutot que de mentir : une valeur ajoutee demain sera visible
    // dans le rapport, et le test `No_raw_identifier_reaches_the_client` la fera rougir.
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

    private static string Texte(BottleneckState state) => state switch
    {
        BottleneckState.UnknownPartial => Docteur.T("Rapport.Etat.NonClasse"),
        BottleneckState.GpuBound => Docteur.T("Rapport.Etat.Gpu"),
        BottleneckState.MixedGpuLeaning => Docteur.T("Rapport.Etat.PlutotGpu"),
        BottleneckState.CpuBoundOrAppBound => Docteur.T("Rapport.Etat.CpuOuJeu"),
        _ => state.ToString(),
    };

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
        _ => architecture.ToString(),
    };
}
