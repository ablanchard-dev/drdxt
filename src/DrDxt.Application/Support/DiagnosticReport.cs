using System.Globalization;
using System.Text;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Support;

/// <summary>
/// D7 / gate G6 — le rapport que le client colle dans son ticket.
///
/// Sans lui, le premier client difficile consomme une semaine : on débogue à l'aveugle par mail,
/// une question à la fois, avec quelqu'un qui ne sait pas où regarder. Un dev solo qui vend à des
/// joueurs un produit qui les envoie dans leur BIOS ne peut pas se permettre ça.
///
/// Le format est du TEXTE BRUT, pas du JSON : le client le colle dans un Discord ou un mail, et
/// il doit pouvoir le relire lui-même avant de l'envoyer. Un blob illisible ne se relit pas, donc
/// il s'envoie sans être lu — et c'est comme ça qu'on publie ce qu'on ne voulait pas publier.
///
/// ══ ce qui n'entre jamais dans ce fichier ══
///
/// **La clé de licence.** C'est la règle du fichier. Ce rapport est destiné à être colle dans
/// un salon public : une clé qui s'y trouverait serait publiée par le client lui-même, en croyant
/// bien faire. Le rapport porte l'EMPREINTE machine — dérivée, non secrète, et c'est elle que le
/// support utilise pour retrouver une licence.
///
/// Rien de nominatif non plus : ni nom d'utilisateur, ni chemins personnels. Ce qu'on met est ce
/// dont le support a besoin pour comprendre une machine, et rien de plus.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>Le nom du fichier propose. Date dans le nom : deux rapports d'un meme client a
    /// deux moments differents racontent une histoire que le second seul ne raconte pas.</summary>
    public static string FileNameFor(DateTimeOffset when) =>
        FormattableString.Invariant($"drdxt-diagnostic-{when:yyyy-MM-dd-HHmm}.txt");

    /// <summary>
    /// Construit le rapport. Pur : meme entree, meme texte — aucune lecture de machine ici, tout
    /// arrive deja lu, ce qui le rend entierement testable.
    /// </summary>
    /// <param name="scan">Le dernier scan. null = aucun scan encore lance, et ca se dit.</param>
    /// <param name="appVersion">La version du produit — la premiere question de tout support.</param>
    /// <param name="when">L'instant du rapport, injecte (jamais DateTime.Now ici).</param>
    /// <param name="sante">Lot 1 « preuve du besoin » — le tri de l'audit de sante (attention / non
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

        sb.AppendLine("=== DrDXT : rapport de diagnostic ===");
        Line(sb, "Version", appVersion);
        // 06/10/2026 (plan 4.6) : le runtime EMBARQUÉ ne suit pas les correctifs de la machine ; le support doit le lire.
        Line(sb, "Runtime .NET", Environment.Version.ToString());
        Line(sb, "Généré le", when.ToString("u", CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (scan is null)
        {
            sb.AppendLine("Aucun scan n'a encore été lancé sur cette machine.");
            sb.AppendLine("Lance un scan, puis exporte à nouveau : sans lui, ce rapport ne dit rien d'utile.");
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
        // 26/09/2026 : l'écran montre le débit et la latence mesurés de la mémoire, et depuis ce soir la réserve
        // d'une référence prise pendant une partie ; le rapport n'en disait rien. La ligne de l'écran, telle quelle.
        AppendSection(sb, "--- Mémoire mesurée ---", memoire?.Phrase);
        AppendSection(sb, "--- Réseau mesuré ---", reseau?.Phrase);
        AppendMeasurement(sb, scan.Baseline);
        // 27/09/2026 : la dérive (ce jeu, cette machine, depuis la première mesure) : à l'écran, absente d'ici. C'est
        // pourtant la réponse à « mon PC a-t-il ralenti ? », la première question d'un joueur au support.
        AppendSection(sb, "--- Dérive des performances ---", derive?.Phrase);
        // 27/09/2026 : la borne du gain, affichée à l'écran (domaine Carte graphique), absente du rapport. Elle se
        // calcule de la mesure du scan, déjà ici : la même phrase, sans rien recevoir de l'appelant.
        AppendSection(sb, "--- Plafond du gain ---", DrDxt.Measurement.PlafondGpu.PhraseDuScan(scan.Baseline?.GpuBusyPercent));

        return sb.ToString();
    }

    private static void AppendMachine(StringBuilder sb, HardwareInventory hw)
    {
        sb.AppendLine("--- Machine ---");
        // Le nombre de CCD n'est imprime que s'il en existe. Vu sur un vrai rapport de cette
        // machine : « 20c/28t, 0 CCD » sur un Intel, ou la notion n'existe pas. Un champ sans
        // objet affiche a zero se lit comme une detection ratee, et fait perdre une minute au
        // support sur un probleme qui n'existe pas.
        var ccd = hw.Cpu.CcdCount > 0 ? $", {hw.Cpu.CcdCount} CCD" : string.Empty;
        Line(sb, "Processeur", $"{hw.Cpu.Model} ({hw.Cpu.Cores}c/{hw.Cpu.Threads}t{ccd})");
        foreach (var gpu in hw.Gpus)
            Line(sb, "Carte graphique", $"{gpu.Model}, pilote {gpu.DriverVersion}");
        foreach (var stick in hw.Memory)
            // MT/s, pas MHz. `ScanViewModel` porte deja cette correction : 3800 MT/s = 1900 MHz
            // reels, et l'etiquette MHz est fausse pour une DDR. L'ecran avait ete corrige, pas
            // ce rapport — et c'est lui que le client colle dans son ticket, donc lui qui voyage.
            Line(sb, "Mémoire", $"{stick.Manufacturer} {stick.PartNumber}, {stick.ConfiguredMhz} MT/s (nominal {stick.RatedMhz})");
        Line(sb, "Carte mère", $"{hw.Board.Manufacturer} {hw.Board.Product}");
        Line(sb, "BIOS", hw.Bios.Version);
        Line(sb, "Châssis", Fr(hw.Chassis));
        sb.AppendLine();
    }

    // 24/09/2026 — ce que la NVRAM du BIOS dit, quand elle a pu etre lue. Sinon on le DIT : les
    // etats XMP/ReBAR/CSM du rapport sont alors des deductions, pas des lectures.
    private static void AppendBiosLu(StringBuilder sb, SettingsState? st)
    {
        sb.AppendLine("--- Réglages lus dans le BIOS ---");
        if (st?.BiosLu is not { Count: > 0 } lus)
        {
            sb.AppendLine("Non lus : carte ou version de BIOS absente de la table, ou lecture impossible sans droits administrateur. XMP, ReBAR et CSM sont déduits de ce que Windows voit.");
        }
        else
        {
            foreach (var l in lus)
                Line(sb, l.Setting, $"{l.Libelle} ({(l.IsDefault ? "réglage d'usine" : "modifié")})");
        }
        sb.AppendLine();
    }

    private static void AppendRegister(StringBuilder sb, ScanResult scan)
    {
        sb.AppendLine("--- Le registre ---");
        var problems = scan.Ledger?.Count(e => e.State == LeverState.Suboptimal) ?? 0;
        // 25/09/2026 : « Problèmes prouvés : 0 » suivi d'un constat « à corriger » se contredisait à
        // la lecture ; ce compte ne porte que sur les leviers, l'intitulé le dit.
        Line(sb, "Leviers à corriger", problems.ToString(CultureInfo.InvariantCulture));
        Line(sb, "Leviers lus", scan.Coverage is { } c
            ? FormattableString.Invariant($"{c.KnownLevers} sur {c.ApplicableLevers} ({c.UnknownLevers} non lus)")
            : "(couverture non calculée)");
        sb.AppendLine();
    }

    /// <summary>
    /// Lot 1 « preuve du besoin » — la sante de la machine, telle que l'ecran l'a triee.
    ///
    /// Le rapport genere sur la machine de reference le 10/09 ne portait aucun de ses vrais
    /// problemes : ni les arrets brutaux, ni la memoire jamais testee. Ils vivaient a l'ecran et
    /// n'arrivaient pas dans le document que le client garde. La phrase a trois nombres vient de
    /// <see cref="RepairTriage.Phrase"/> : ce qui merite l'attention, ce qui est verifie, ce qui n'a
    /// pas pu etre lu — jamais l'un sans les autres.
    /// </summary>
    private static void AppendSante(StringBuilder sb, RepairTriage? sante)
    {
        sb.AppendLine("--- Santé de la machine ---");
        if (sante is null)
        {
            // Un audit non lance n'est pas une machine saine.
            sb.AppendLine("(audit de santé non lancé : rien n'est affirmé sur la santé de cette machine)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(sante.Phrase);
        foreach (var c in sante.Attention)
            Line(sb, $"{RepairLibelles.Domaine(c.Domaine)} · {RepairLibelles.Nom(c.Nom)}",
                (c.ValeurCourante ?? "non lisible")
                + (c.ValeurAttendue is { } attendue ? $" (attendu : {attendue})" : string.Empty)
                + $" : {c.Explication}");
        if (sante.NonLus.Count > 0)
            Line(sb, "Non lus", string.Join(", ", sante.NonLus.Select(c => $"{RepairLibelles.Domaine(c.Domaine)} · {RepairLibelles.Nom(c.Nom)}")));
        // 24/09/2026 : les états lus sans geste portent aussi une explication sourcée (fréquence
        // effective, bridage, incidents noyau, MMCSS…). L'écran n'en montre que la valeur ; ici,
        // dans le document que le client garde, ils ont leur phrase.
        if (sante.Verifies.Count > 0)
        {
            sb.AppendLine("États lus, sans geste proposé :");
            foreach (var c in sante.Verifies)
                Line(sb, $"{RepairLibelles.Domaine(c.Domaine)} · {RepairLibelles.Nom(c.Nom)}",
                    (c.ValeurCourante ?? "non lisible") + (string.IsNullOrWhiteSpace(c.Explication) ? "" : $" : {c.Explication}"));
        }
        sb.AppendLine();
    }

    /// <summary>
    /// 25/09/2026 — les constats du scanner, avec leur texte. Relu sur un rapport réel : le constat
    /// principal de la machine de référence (le pilote graphique en performances maximales pour tout
    /// le PC) n'y figurait pas ; seul « vrr : à corriger » passait, par le détail des leviers. Sans
    /// traducteur (appels de test historiques), la section se tait plutôt que d'écrire des clés.
    /// </summary>
    private static void AppendConstats(StringBuilder sb, ScanResult scan, Func<string, string>? localiser)
    {
        if (localiser is null || scan.Findings.Count == 0) return;
        sb.AppendLine("--- Constats du scan ---");
        foreach (var f in scan.Findings.OrderByDescending(f => f.Severity))
        {
            var gravite = f.Severity switch
            {
                Severity.Major => "important",
                Severity.Minor => "à corriger",
                _ => "à savoir",
            };
            var domaine = f.Category switch
            {
                "Gpu" => "Carte graphique",
                "Cpu" => "Processeur",
                "Memory" => "Mémoire",
                "Latency" => "Latence",
                "Network" => "Réseau",
                "Display" => "Écran",
                "Storage" => "Stockage",
                _ => f.Category,
            };
            Line(sb, $"{domaine} · {gravite}", localiser(f.TitleKey));
        }
        sb.AppendLine();
    }

    private static void AppendLedger(StringBuilder sb, ScanResult scan)
    {
        if (scan.Ledger is not { Count: > 0 }) return;

        sb.AppendLine("--- Détail par levier ---");
        foreach (var entry in scan.Ledger)
            // L'identifiant du levier et son etat, pas le libelle traduit : le support lit ce
            // fichier, et un identifiant se cherche dans le code alors qu'une phrase non.
            Line(sb, entry.LeverId, Fr(entry.State));
        sb.AppendLine();
    }

    private static void AppendBottleneck(StringBuilder sb, ScanResult scan)
    {
        sb.AppendLine("--- Ce qui limite cette machine ---");
        if (scan.Bottleneck is not { } verdict)
        {
            sb.AppendLine("(non classé)");
            sb.AppendLine();
            return;
        }

        Line(sb, "État", Fr(verdict.State));

        // Une confiance ne s'affiche pas sans la chose qu'elle qualifie. Vu en usage reel le
        // 05/09/2026 : « Confiance : High » imprime sous « Etat : UnknownPartial », ce qui se lit
        // « grande confiance dans un etat inconnu ». La confiance qualifie une CLASSIFICATION ;
        // quand il n'y a pas de classification, elle ne qualifie rien.
        if (verdict.State != BottleneckState.UnknownPartial)
        {
            Line(sb, "Confiance", Fr(verdict.Confidence));
        }

        Line(sb, "Provisoire", verdict.Provisional ? "oui" : "non");
        // Le MOTIF en clair : c'est lui qui permet au support de comprendre le verdict sans
        // rejouer le scan. Un etat sans son motif est un code, pas un diagnostic.
        Line(sb, "Motif", MotifFr(verdict));
        sb.AppendLine();
    }

    private static void AppendCoverage(StringBuilder sb, HardwareInventory hw)
    {
        var rows = GpuTuningCoverage.For(hw);
        if (rows.Count == 0) return;

        // 25/09/2026 : la section annonçait « réglable » sans dire à quelles conditions. 06/10/2026 (L-GPU) : le réglage
        // est ouvert machine par machine ; ce qui le ferme est dit, sans rien promettre.
        sb.AppendLine("--- Réglage de la carte graphique ---");
        sb.AppendLine("Essayé en jeu par Optimiser, gardé seulement avec un gain mesuré, sinon annulé ; fermé sur une "
            + "machine dont un arrêt récent touche la carte (écran bleu graphique, arrêts secs répétés). Compatibilité lue :");
        foreach (var row in rows)
            Line(sb, row.Model,
                FormattableString.Invariant(
                    $"{Fr(row.Support)}, {Fr(row.Architecture)}, voltage manuel : {(row.VoltageTuning ? "oui" : "non")}"));
        sb.AppendLine();
    }

    /// <summary>25/09/2026 — ce que le processeur rend sous charge longue. Affiche a l'ecran, absent du
    /// rapport jusqu'ici (relu dans le rapport reel) : c'est pourtant ce qui explique un jeu limite par le
    /// processeur. Rien n'est ecrit quand la mesure n'existe pas.</summary>
    private static void AppendProcesseurSoutenu(StringBuilder sb, string? phrase)
        => AppendSection(sb, "--- Processeur sous charge longue ---", phrase);

    /// <summary>25/09/2026 : la ligne réseau de l'écran, jusque-là absente du rapport.</summary>
    private static void AppendSection(StringBuilder sb, string titre, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return;
        sb.AppendLine(titre);
        sb.AppendLine(phrase);
        sb.AppendLine();
    }

    private static void AppendMeasurement(StringBuilder sb, BenchmarkResult? baseline)
    {
        sb.AppendLine("--- Mesure ---");
        if (baseline is null)
        {
            // Une mesure absente n'est pas une mesure a zero. Le dire evite au support de
            // chercher pourquoi les chiffres sont mauvais alors qu'il n'y en a pas.
            // 24/09/2026 : PresentMon n'est plus le seul instrument (repli DXGI) ; ne pas l'accuser seul.
            sb.AppendLine("Aucune mesure : aucun jeu en cours au moment du scan, ou aucun instrument n'a pu compter les images.");
            sb.AppendLine();
            return;
        }

        // 24/09/2026 : qui a compte. Le repli DXGI ne voit ni la latence ni le focus : le rapport le dit.
        Line(sb, "Instrument", baseline.Instrument is "DXGI"
            ? "compteur DXGI de DrDXT (PresentMon n'a rien capturé ; latence et focus non mesurés)"
            : "PresentMon");
        Line(sb, "Processus mesuré", baseline.ProcessusMesure ?? "inconnu");
        Line(sb, "FPS moyen", baseline.AvgFps.ToString("F1", CultureInfo.InvariantCulture));
        Line(sb, "1% low", baseline.P1LowFps.ToString("F1", CultureInfo.InvariantCulture));
        // 27/09/2026 : les lows ont leur propre marge (plus large que celle de la moyenne) ; sans elle, le chiffre
        // se lisait aussi solide que le FPS moyen.
        Line(sb, "Marge (1% low)", double.IsFinite(baseline.P1LowMarginOfError)
            ? baseline.P1LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : "inconnue (moins de 3 passes)");
        Line(sb, "0.1% low", baseline.P01LowFps.ToString("F1", CultureInfo.InvariantCulture));
        Line(sb, "Marge (0.1% low)", double.IsFinite(baseline.P01LowMarginOfError)
            ? baseline.P01LowMarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : "inconnue (moins de 3 passes)");
        // La marge et le nombre de passes sans lesquels un chiffre de FPS ne veut rien dire.
        // 26/09/2026 : +inf sous trois passes, que la culture invariante ecrit « Infinity ».
        Line(sb, "Marge d'erreur", double.IsFinite(baseline.MarginOfError)
            ? baseline.MarginOfError.ToString("F2", CultureInfo.InvariantCulture)
            : "inconnue (moins de 3 passes)");
        Line(sb, "Passes", baseline.Runs.ToString(CultureInfo.InvariantCulture));
        Line(sb, "Frames capturées", baseline.FrameTimesMs.Count.ToString(CultureInfo.InvariantCulture));
        // 27/09/2026 : les queues que l'écran affiche (domaine Latence), absentes jusqu'ici. Une moyenne cache la
        // saccade ; le support qui ne voit que le FPS moyen ne voit pas ce que le joueur ressent.
        LigneQueue(sb, "Trames lentes (p95)", DrDxt.Measurement.FrametimeStatistics.SaccadeP95(baseline));
        LigneQueue(sb, "Écart d'animation (p95)", DrDxt.Measurement.FrametimeStatistics.CadenceP95(baseline));
        LigneQueue(sb, "Attente d'affichage (p95)", DrDxt.Measurement.FrametimeStatistics.AttenteP95(baseline));
        sb.AppendLine();
    }

    private static void LigneQueue(StringBuilder sb, string label, double? ms)
    {
        if (ms is { } v) Line(sb, label, v.ToString("F2", CultureInfo.InvariantCulture) + " ms");
    }

    // 25/09/2026 : « {label,-22}: » collait les deux-points aux intitulés plus longs que la
    // colonne (« Long Duration Power Limit (PL1): Auto »). Même sortie pour les intitulés courts.
    private static void Line(StringBuilder sb, string label, string value) =>
        sb.AppendLine(FormattableString.Invariant($"{label,-21} : {value}"));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Les valeurs affichees, en francais
    //
    // Mesure du 05/09/2026, en usage reel sur une vraie machine : ce rapport imprimait
    // « Chassis : Desktop », « Etat : UnknownPartial », « Confiance : High »,
    // « Validated — AdaOrOlder ». Ce sont des noms de symboles C#, dans un texte que le client lit
    // et colle dans un salon public. Un identifiant brut n'est pas une information : c'est du code
    // qui a fuit jusqu'a l'utilisateur, et ca demande au client de deviner ce que le programme
    // voulait dire.
    //
    // Les libelles sont ici et pas dans le resx : ce fichier porte deja tout son texte en dur
    // (47 chaines mesurees le 05/09), et melanger deux sources pour un meme document rendrait la
    // relecture impossible. Le jour ou le rapport devient multilingue, tout part au resx ensemble.
    //
    // Le `_ =>` rend le nom brut plutot que de mentir : une valeur ajoutee demain sera VISIBLE
    // dans le rapport, et le test `No_raw_identifier_reaches_the_client` la fera rougir.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Le motif du limiteur, en francais, rendu depuis la CLE et ses valeurs.
    ///
    /// Le classifieur ne construit plus de phrase pour le client : il rend un fait et ses
    /// grandeurs, et chaque bord l'ecrit dans sa langue. Le formatage des nombres appartient donc
    /// ici : « 96 % » et « 96.0% » ne s'ecrivent pas pareil selon la langue, et c'est le rendu qui
    /// le sait, pas le classifieur.
    ///
    /// Cle absente ou inconnue -> on rend le motif technique ANGLAIS plutot que rien. C'est laid
    /// et c'est voulu : un motif manquant doit se voir dans le rapport, et le test
    /// `The_motif_reaches_the_client_in_French` le fera rougir avant qu'un client ne le lise.
    /// </summary>
    private static string MotifFr(BottleneckVerdict verdict)
    {
        var v = verdict.ReasonValues ?? Array.Empty<double>();
        string Pct(int i) => i < v.Count ? (v[i] * 100).ToString("F0", CultureInfo.GetCultureInfo("fr-FR")) : "?";
        string Ms(int i) => i < v.Count ? v[i].ToString("F1", CultureInfo.GetCultureInfo("fr-FR")) : "?";

        return verdict.ReasonKey switch
        {
            BottleneckReasonKeys.NoMeasurement =>
                "Aucune mesure en jeu n'a été prise : rien n'est affirmé sur ce qui limite cette machine en jeu.",

            BottleneckReasonKeys.GpuBusyUnmeasured =>
                "Le temps d'occupation du GPU n'a pas pu être mesuré (anti-triche, suivi GPU absent, "
                + "ou trop peu d'images) : aucun limiteur n'est désigné, et seuls des réglages sans "
                + "risque sont proposés.",

            BottleneckReasonKeys.CommitPressure =>
                $"La mémoire validée atteint {Pct(0)} % de la limite système. Microsoft documente des "
                + "gels et des plantages quand cette limite est atteinte. C'est un problème de "
                + "fiabilité, pas de nombre d'images : aucun gain de FPS n'est promis en le corrigeant.",

            BottleneckReasonKeys.Capped =>
                "Le nombre d'images est plafonné (limiteur ou synchronisation verticale) : la machine "
                + "ne montre pas ce qu'elle sait faire, et toute comparaison avant/après sur la "
                + "moyenne serait une illusion tant que le plafond tient.",

            BottleneckReasonKeys.Throttling =>
                "Le GPU signale une limite de température ou de puissance : il donne déjà ce qu'il "
                + "peut. Plus de puissance ou de fréquence n'est pas la réponse tant qu'il n'a pas "
                + "de marge thermique.",

            BottleneckReasonKeys.FramePacing =>
                "Le débit moyen va, mais la cadence est irrégulière : c'est cette irrégularité que "
                + "le joueur ressent, pas la moyenne.",

            BottleneckReasonKeys.GpuBound =>
                $"Le GPU a produit {Pct(0)} % de chaque image sans presque attendre : c'est lui la "
                + "limite. Les promesses côté processeur et côté fréquence mémoire sont donc "
                + "écartées pour cette machine.",

            BottleneckReasonKeys.CpuBoundStrong =>
                $"Le GPU n'a produit que {Pct(0)} % de chaque image et a passé le reste à attendre : "
                + "c'est le processeur ou le jeu lui-même qui donne le rythme. Toucher à la "
                + "puissance ou à la fréquence du GPU ne rendra pas d'images ici.",

            BottleneckReasonKeys.CpuOrAppBound =>
                $"Le GPU a produit {Pct(0)} % de chaque image en attendant du travail, ce qui désigne "
                + "le processeur ou le jeu plutôt que la carte graphique.",

            BottleneckReasonKeys.MixedGpuLeaning =>
                $"Le GPU a produit {Pct(0)} % de chaque image : il est plutôt la limite, sans que ce "
                + "soit assez net pour l'affirmer. Un réglage GPU peut aider, et devra être prouvé "
                + "par une comparaison avant/après avant d'être gardé.",

            BottleneckReasonKeys.LatencyOnly =>
                $"Le débit et la cadence vont bien, mais la latence d'affichage monte à {Ms(0)} ms "
                + $"pour une image médiane de {Ms(1)} ms. Ce qui reste à gagner ici est de la "
                + "réactivité, pas des images.",

            BottleneckReasonKeys.BetweenStates =>
                $"Le GPU a produit {Pct(0)} % de chaque image, ce qui tombe entre les états que ce "
                + "classifieur accepte de nommer. Aucun limiteur n'est désigné.",

            _ => verdict.Reason,
        };
    }

    private static string Fr(ChassisType chassis) => chassis switch
    {
        ChassisType.Desktop => "ordinateur fixe",
        ChassisType.Laptop => "portable",
        ChassisType.Handheld => "console portable",
        ChassisType.Unknown => "non déterminé",
        _ => chassis.ToString(),
    };

    private static string Fr(LeverState state) => state switch
    {
        LeverState.Optimal => "bien réglé",
        LeverState.Suboptimal => "à corriger",
        LeverState.Unknown => "non lu",
        LeverState.NotApplicable => "sans objet sur cette machine",
        _ => state.ToString(),
    };

    private static string Fr(BottleneckState state) => state switch
    {
        BottleneckState.UnknownPartial => "non classé",
        BottleneckState.GpuBound => "limité par le GPU",
        BottleneckState.MixedGpuLeaning => "plutôt limité par le GPU, sans certitude",
        BottleneckState.CpuBoundOrAppBound => "limité par le CPU ou par le jeu",
        _ => state.ToString(),
    };

    private static string Fr(BottleneckConfidence confidence) => confidence switch
    {
        BottleneckConfidence.Low => "faible",
        BottleneckConfidence.Medium => "moyenne",
        BottleneckConfidence.High => "élevée",
        _ => confidence.ToString(),
    };

    private static string Fr(GpuTuningSupport support) => support switch
    {
        GpuTuningSupport.Validated => "réglable, validé sur cette architecture",
        GpuTuningSupport.ReadOnly => "aucune validation en écriture archivée pour cette architecture",
        GpuTuningSupport.NotApplicable => "rien à régler sur ce matériel",
        _ => support.ToString(),
    };

    private static string Fr(GpuArchitecture architecture) => architecture switch
    {
        GpuArchitecture.Unknown => "architecture non reconnue",
        GpuArchitecture.AdaOrOlder => "NVIDIA Ada (RTX 40) ou antérieure",
        GpuArchitecture.Blackwell => "NVIDIA Blackwell (RTX 50)",
        GpuArchitecture.Rdna3 => "AMD RDNA3",
        GpuArchitecture.Rdna2 => "AMD RDNA2",
        GpuArchitecture.Rdna4 => "AMD RDNA4",
        _ => architecture.ToString(),
    };
}
