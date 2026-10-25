using DrDxt.Application.Support;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Application.Tests.Support;

// -----------------------------------------------------------------------
// Le rapport que le client colle dans son ticket.
//
// « Jamais de debogage a l'aveugle par mail. » Sans ce rapport, le premier client difficile
// consomme une semaine : une question a la fois, avec quelqu'un qui ne sait pas ou regarder.
//
// La regle du fichier : ce rapport est fait pour etre colle dans un salon public. Une cle de
// licence qui s'y trouverait serait publiee par le client lui-meme, en croyant bien faire.
// -----------------------------------------------------------------------
public sealed class DiagnosticReportTests
{
    private const string Secret = "DRDXT-CLE-SECRETE-0001";
    private static readonly DateTimeOffset When = new(2026, 9, 2, 7, 30, 0, TimeSpan.Zero);

    private static HardwareInventory Hw() => new(
        new CpuInfo("Intel Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "551.86", TriState.On) },
        new[] { new MemoryModule("F4-4000C18-32GTES", "G.Skill", 3800, 4000) },
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.H0", When, true),
        ChassisType.Desktop);

    private static ScanResult Scan(
        BenchmarkResult? baseline = null,
        BottleneckVerdict? bottleneck = null,
        IReadOnlyList<ScoreLedgerEntry>? ledger = null,
        ScanCoverage? coverage = null) =>
        new(Hw(),
            new SettingsState(TriState.Off, TriState.On, "guid", TriState.On, TriState.On,
                Array.Empty<int>(), TriState.Unknown),
            Array.Empty<Finding>(), baseline,
            ledger ?? new[]
            {
                new ScoreLedgerEntry("xmp", "Performance", 15, LeverState.Suboptimal, "k", "s"),
                new ScoreLedgerEntry("rebar", "Performance", 0, LeverState.Optimal, "k", "s"),
                new ScoreLedgerEntry("x3d-ccd", "Performance", 0, LeverState.Unknown, "k", "s"),
            },
            coverage ?? new ScanCoverage(3, 2, 1),
            bottleneck);

    [Fact]
    public void En_anglais_le_rapport_n_a_plus_un_mot_de_francais()
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        var rapport = DiagnosticReport.Build(Scan(), "1.0", When);

        rapport.Should().Contain("diagnostic report").And.Contain("Graphics card").And.Contain("Levers to fix");
        rapport.Should().NotMatchRegex("[éèàùçêâîôû]");
    }

    [Fact]
    public void Le_rapport_dit_ce_que_le_BIOS_a_rendu_et_le_dit_quand_il_n_a_rien_rendu()
    {
        var lu = Scan() with
        {
            State = Scan().State with
            {
                BiosLu = new[] { new BiosSettingReading("XMP", 1, "Profile 1", IsDefault: false) },
            },
        };
        DiagnosticReport.Build(lu, "1.0", When).Should().Contain("XMP").And.Contain("Profile 1 (modifié)");

        // Rien de lu : le rapport ne se tait pas, il dit que XMP/ReBAR/CSM sont deduits.
        DiagnosticReport.Build(Scan(), "1.0", When).Should().Contain("sont déduits de ce que Windows voit");
    }

    [Fact]
    public void Le_rapport_porte_ce_que_le_processeur_rend_sous_charge_longue()
    {
        // Affiche a l'ecran, absent du rapport (relu dans le rapport reel).
        const string phrase = "Sous une charge longue sur 8 cœurs, ton processeur tient 30,0 % de puissance en moins.";
        DiagnosticReport.Build(Scan(), "1.0", When, processeur: new DrDxt.Measurement.Cpu.CpuDeficitSoutenu(30, 7.4, true, phrase))
            .Should().Contain("--- Processeur sous charge longue ---").And.Contain(phrase);
        DiagnosticReport.Build(Scan(), "1.0", When)
            .Should().NotContain("Processeur sous charge longue", "sans mesure, pas de section vide");
    }

    [Fact]
    public void Le_rapport_ne_promet_pas_un_reglage_de_la_carte_sans_ses_conditions()
    {
        // « Ce que cette machine reçoit : RTX 4060 réglable », sans condition. Le
        // réglage est ouvert machine par machine ; le rapport dit à quelles conditions il est gardé ou fermé.
        DiagnosticReport.Build(Scan(), "1.0", When)
            .Should().Contain("--- Réglage de la carte graphique ---")
            .And.Contain("gardé seulement avec un gain mesuré, sinon annulé")
            .And.Contain("écran bleu graphique")
            .And.NotContain("Ce que cette machine reçoit");
    }

    [Fact]
    public void Le_rapport_nomme_l_instrument_qui_a_compte_les_images()
    {
        var serie = DrDxt.Measurement.FrametimeStatistics.PasseDepuisSerie(Enumerable.Repeat(5.0, 100).ToList(), 1, 0);
        DiagnosticReport.Build(Scan(baseline: serie with { Instrument = "DXGI" }), "1.0", When)
            .Should().Contain("compteur DXGI").And.Contain("latence et focus non mesurés");
        DiagnosticReport.Build(Scan(baseline: serie), "1.0", When).Should().Contain("Instrument").And.Contain("PresentMon");
        DiagnosticReport.Build(Scan(), "1.0", When).Should().NotContain("PresentMon indisponible");
    }

    [Fact]
    public void Le_rapport_nomme_le_processus_mesure()
    {
        // 99 FPS sur un scan cru « sans jeu », c'etait JeuB au premier plan.
        var serie = DrDxt.Measurement.FrametimeStatistics.PasseDepuisSerie(Enumerable.Repeat(5.0, 100).ToList(), 1, 0);
        DiagnosticReport.Build(Scan(baseline: serie with { ProcessusMesure = "JeuBClient-Win64-Shipping.exe" }), "1.0", When)
            .Should().MatchRegex(@"Processus mesuré\s+: JeuBClient-Win64-Shipping\.exe");
        DiagnosticReport.Build(Scan(baseline: serie), "1.0", When).Should().MatchRegex(@"Processus mesuré\s+: inconnu");
    }

    // ====================== La preuve porte la SANTE ======================

    [Fact]
    public void La_sante_de_la_machine_arrive_dans_le_rapport_avec_ses_trois_nombres()
    {
        // Les vrais problemes de la machine (arrets brutaux, memoire jamais testee) arrivent dans le
        // rapport, pas seulement a l'ecran.
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("STABILITE", "arrets brutaux", "8 en 45 jours", "aucun",
                RepairStatus.Suspicious, "un arret sec ne laisse aucun dump"),
            new RepairFinding("STOCKAGE", "usure", "0 %", null, RepairStatus.ExpectedDefault, "disque sain"),
            new RepairFinding("BIOS", "XMP", null, null, RepairStatus.Unknown, "non lisible"),
        });

        var report = DiagnosticReport.Build(Scan(), "1.0.0", When, sante);

        report.Should().Contain("--- Santé de la machine ---");
        report.Should().Contain(sante.Phrase, "les trois nombres voyagent ensemble");
        // Les memes mots que l'ecran, plus les cles du journal.
        report.Should().Contain("Stabilité · Arrêts brutaux").And.Contain("8 en 45 jours")
            .And.NotContain("STABILITE");
        report.Should().Contain("Non lus").And.Contain("BIOS · XMP");
    }

    /// <summary>Un nom porté par deux familles garde son titre de famille dans le rapport, comme à l'écran.</summary>
    [Fact]
    public void Le_rapport_nomme_les_echecs_par_famille()
    {
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("SERVICES", "echecs", "3", null, RepairStatus.Suspicious, "trois services en échec."),
            new RepairFinding("WINDOWS UPDATE", "echecs", "2", null, RepairStatus.Observed, "deux mises à jour en échec."),
        });

        DiagnosticReport.Build(Scan(), "1.0.0", When, sante).Should()
            .Contain("Échecs de services").And.Contain("Échecs de Windows Update");
    }

    /// <summary>Un nom qui dit déjà son domaine ne le répète pas : « Mises à jour Windows · mises à jour Windows en attente ».</summary>
    [Fact]
    public void Le_rapport_ne_repete_pas_un_domaine_que_le_nom_dit_deja()
    {
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("WINDOWS UPDATE", "en attente", "aucune", null, RepairStatus.Observed, "rien en attente."),
        });

        var rapport = DiagnosticReport.Build(Scan(), "1.0.0", When, sante);
        rapport.Should().Contain("Mises à jour Windows en attente").And.NotContain("Mises à jour Windows · Mises à jour Windows");
    }

    /// <summary>Un état lu sans geste peut porter ses étapes (le test mémoire jamais lancé) : elles arrivent dans le rapport.</summary>
    [Fact]
    public void Les_etapes_d_un_etat_lu_arrivent_dans_le_rapport()
    {
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("STABILITE", "test memoire Windows", "jamais lancé", null, RepairStatus.Observed,
                "aucun résultat du test.", Remede: "1) Windows + R, mdsched.exe."),
        });

        DiagnosticReport.Build(Scan(), "1.0.0", When, sante).Should().Contain("mdsched.exe");
    }

    /// <summary>Le geste a sa propre ligne, sous le constat, à part de l'explication.</summary>
    [Fact]
    public void Le_geste_d_un_constat_a_corriger_a_sa_propre_ligne()
    {
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("STABILITE", "arrets brutaux", "8 en 45 jours", "aucun",
                RepairStatus.Suspicious, "un arret sec ne laisse aucun dump", Remede: "1) Lance le test mémoire. 2) Relis le résultat."),
        });

        var lignes = DiagnosticReport.Build(Scan(), "1.0.0", When, sante).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var i = lignes.FindIndex(l => l.Contains("Arrêts brutaux"));
        lignes[i].Should().NotContain("Lance le test mémoire", "le geste ne se noie plus dans l'explication");
        lignes[i + 1].Should().Contain("Comment corriger").And.Contain("1) Lance le test mémoire. 2) Relis le résultat.");
    }

    /// <summary>L'écran ne montre que la valeur d'un état lu sans geste ; son
    /// explication sourcée n'apparaissait nulle part. Le rapport la porte.</summary>
    [Fact]
    public void Les_etats_lus_sans_geste_arrivent_avec_leur_explication()
    {
        var sante = RepairTriage.Trier(new[]
        {
            new RepairFinding("STABILITE", "incident noyau conserve", "1x 0x144", null,
                RepairStatus.Observed, "vidage à chaud du contrôleur USB 3"),
        });

        var report = DiagnosticReport.Build(Scan(), "1.0.0", When, sante);

        report.Should().Contain("États lus, sans geste proposé")
            .And.Contain("1x 0x144").And.Contain("vidage à chaud du contrôleur USB 3");
    }

    /// <summary>Relu sur un rapport réel, le constat principal (pilote en performances
    /// maximales pour tout le PC) n'y était pas. Avec le traducteur, les constats du scan y sont.</summary>
    [Fact]
    public void Les_constats_du_scan_arrivent_traduits_et_par_gravite()
    {
        var scan = Scan() with
        {
            Findings = new[]
            {
                new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null),
                new Finding("Network", Severity.Info, "Finding.Network.Eee", null),
            },
        };

        var report = DiagnosticReport.Build(scan, "1.0.0", When, localiser: k => $"<{k}>");

        report.Should().Contain("--- Constats du scan ---")
            .And.Contain("Carte graphique · à corriger").And.Contain("<Finding.Gpu.PerfMaxGlobal>")
            .And.Contain("Réseau · à savoir");
        report.IndexOf("Finding.Gpu.PerfMaxGlobal", StringComparison.Ordinal)
            .Should().BeLessThan(report.IndexOf("Finding.Network.Eee", StringComparison.Ordinal), "le plus grave d'abord");
    }

    [Fact]
    public void Sans_traducteur_la_section_des_constats_se_tait()
        => DiagnosticReport.Build(Scan() with { Findings = new[] { new Finding("Gpu", Severity.Minor, "X", null) } },
                "1.0.0", When)
            .Should().NotContain("Constats du scan");

    [Fact]
    public void Sans_audit_le_rapport_le_DIT_au_lieu_de_se_taire()
        => DiagnosticReport.Build(Scan(), "1.0.0", When)
            .Should().Contain("audit de santé non lancé");

    // ====================== la règle du fichier ======================

    /// <summary>
    /// Un chemin absolu, c'est un nom d'utilisateur. Dans un texte fait pour etre colle dans un
    /// salon public, ca vaut la cle de licence : le client publie son identite en croyant bien
    /// faire, et c'est le produit qui l'a mis dans le presse-papiers.
    ///
    /// Mesure du 05/09/2026 : le rapport n'en contient aucun — il ne dit que du materiel et de la
    /// mesure. Mais rien ne gardait le prochain champ : une ligne « Journal » pointant
    /// %LOCALAPPDATA% laissait les 383 tests de cet assembly au VERT.
    ///
    /// Le depot a deja paye cette faute une fois : un chemin absolu portant un nom d'utilisateur
    /// avait du etre retire des marqueurs d'outillage.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AbsolutePath =
        new(@"([A-Za-z]:\\|\\\\[A-Za-z0-9_.-]+\\|/(home|Users)/)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    [Fact]
    public void The_report_never_carries_a_filesystem_path()
    {
        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        AbsolutePath.IsMatch(report).Should().BeFalse(
            "un chemin absolu porte un nom d'utilisateur, et ce rapport est colle dans un salon public");
    }

    [Fact]
    public void The_report_never_carries_the_name_of_the_person_running_it()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user) || user.Length < 3)
        {
            // Machine de CI sans nom d'utilisateur exploitable : le controle n'a rien a dire, et
            // il le dit — jamais un vert obtenu en ne regardant rien.
            return;
        }

        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        report.Contains(user, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
            "le rapport decrit la machine sans nommer la personne");
    }

    [Fact]
    public void The_path_detector_would_actually_catch_one()
    {
        // Controle POSITIF : sans lui, une expression reguliere cassee rendrait la meme couleur
        // qu'un rapport sain — deja arrive deux fois dans cette session.
        AbsolutePath.IsMatch(@"Journal : C:\Users\quelquun\AppData\Local\DrDXT\drdxt.log")
            .Should().BeTrue();
        AbsolutePath.IsMatch(@"Journal : \\serveur\partage\drdxt.log").Should().BeTrue();
        AbsolutePath.IsMatch("Processeur : Intel Core i7-14700F (20c/28t)").Should().BeFalse();
    }

    /// <summary>
    /// Les enumerations que le rapport REND. Liste explicite parce qu'elle est bornee et qu'elle
    /// decrit une intention : ce sont les types dont une valeur atteint l'ecran du client.
    /// </summary>
    private static readonly Type[] RenderedEnums =
    {
        typeof(ChassisType),
        typeof(LeverState),
        typeof(BottleneckState),
        typeof(BottleneckConfidence),
        typeof(DrDxt.Application.Tweaks.Gpu.GpuTuningSupport),
        typeof(DrDxt.Application.Tweaks.Hardware.GpuArchitecture),
    };

    [Fact]
    public void No_raw_identifier_reaches_the_client()
    {
        // Mesure du 05/09/2026, en usage reel : le rapport rendu sur une vraie machine affichait
        // « Chassis : Desktop », « Etat : UnknownPartial », « Confiance : High »,
        // « Validated — AdaOrOlder ». Ce sont des noms de symboles C#, dans un texte que le client
        // lit et colle dans un salon public. Un identifiant brut n'est pas une information : c'est
        // du code qui a fuit jusqu'a l'utilisateur.
        var report = DiagnosticReport.Build(
            Scan(bottleneck: new BottleneckVerdict(
                BottleneckState.UnknownPartial, BottleneckConfidence.High, "motif", true)),
            "1.0.0", When);

        var leaked = RenderedEnums
            .SelectMany(t => Enum.GetNames(t))
            .Distinct(StringComparer.Ordinal)
            .Where(name => report.Contains(name, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        leaked.Should().BeEmpty(
            "le rapport parle a un client, pas a un debogueur : chaque valeur affichee doit avoir "
            + "ete traduite en francais");
    }

    // Le test au-dessus ne construit qu'un état : six des dix états de goulot sortaient en noms C# (« CpuBoundStrong »)
    // sans qu'il rougisse. Chaque valeur de l'énumération est rendue ici.
    public static TheoryData<BottleneckState> TousLesEtats() => new(Enum.GetValues<BottleneckState>());

    [Theory]
    [MemberData(nameof(TousLesEtats))]
    public void Chaque_etat_de_goulot_se_dit_en_mots(BottleneckState etat)
    {
        var report = DiagnosticReport.Build(
            Scan(bottleneck: new BottleneckVerdict(etat, BottleneckConfidence.High, "motif", true)), "1.0.0", When);

        report.Should().NotContain(etat.ToString());
    }

    // DrDXT n'écrit aucune tension de carte graphique (NoVoltageWriteOpExistsGateTests) : ni une carte validée ni une
    // carte en lecture seule ne se lisent « voltage manuel : oui » ou « possible après validation ».
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060")]
    [InlineData("NVIDIA GeForce RTX 4060")]
    public void Aucune_carte_ne_dit_le_voltage_ouvert(string modele)
    {
        var hw = Hw() with { Gpus = new[] { new GpuInfo(modele, "NVIDIA", 8L << 30, "551.86", TriState.On) } };

        var report = DiagnosticReport.Build(Scan() with { Hw = hw }, "1.0.0", When);

        var ligne = report.Split('\n').Single(l => l.Contains(modele) && l.Contains(Docteur.T("Rapport.VoltageJamaisEcrit")));
        ligne.Should().NotContain(Docteur.T("Rapport.Oui"));
    }

    [Fact]
    public void A_confidence_is_never_shown_without_the_thing_it_qualifies()
    {
        // « Confiance : High » sous « Etat : UnknownPartial » se lit « grande confiance dans un
        // etat inconnu ». La confiance qualifie une CLASSIFICATION ; sans classification, elle ne
        // qualifie rien et n'a rien a faire a l'ecran.
        var report = DiagnosticReport.Build(
            Scan(bottleneck: new BottleneckVerdict(
                BottleneckState.UnknownPartial, BottleneckConfidence.High, "motif", true)),
            "1.0.0", When);

        report.Should().NotContain("Confiance",
            "un etat non classe ne porte aucune confiance affichable");
    }

    [Fact]
    public void A_classified_state_DOES_show_its_confidence()
    {
        // L'autre bord : quand l'etat est classe, la confiance est une information utile au
        // support, et la retirer partout serait la correction paresseuse.
        var report = DiagnosticReport.Build(
            Scan(bottleneck: new BottleneckVerdict(
                BottleneckState.GpuBound, BottleneckConfidence.High, "motif", false)),
            "1.0.0", When);

        report.Should().Contain("Confiance");
    }

    /// <summary>
    /// Des mots-outils anglais. Aucun n'existe en francais, et ils apparaissent dans toute phrase
    /// anglaise de plus de quelques mots : c'est un detecteur grossier, et c'est exactement ce
    /// qu'il faut ici — on ne cherche pas a noter une traduction, on cherche a savoir si de
    /// l'anglais atteint le client.
    /// </summary>
    private static readonly string[] EnglishTells =
        [" the ", " was ", " and ", " with ", " which ", " while ", " what ", " each ", " nothing "];

    [Fact]
    public void Every_reason_key_the_classifier_can_emit_is_rendered_in_French()
    {
        // Mesure du 05/09/2026, en usage reel : le rapport francais rendu sur une vraie machine
        // affichait « Motif : No measurement was taken, so nothing is claimed about what limits
        // this machine. » Le classifieur construisait ses phrases en anglais et le rapport les
        // recopiait.
        //
        // Ce controle ne teste pas un motif : il enumere par reflexion toutes les cles que le
        // classifieur peut emettre, et exige que chacune rende du francais. Une cle ajoutee
        // demain sans sa traduction fera rougir ce test — c'est la lecon des cinq perimetres
        // aveugles de la boucle de verification, appliquee ici.
        var keys = typeof(BottleneckReasonKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        keys.Should().NotBeEmpty("sans cle a verifier, ce controle serait vert pour rien");

        var offenders = new List<string>();
        foreach (var key in keys)
        {
            // Des valeurs plausibles : les phrases qui interpolent en portent au plus deux.
            var verdict = new BottleneckVerdict(
                BottleneckState.GpuBound, BottleneckConfidence.High,
                "TECHNICAL ENGLISH REASON — must not reach the client",
                Provisional: false, ReasonKey: key, ReasonValues: new[] { 0.96, 14.2 });

            var report = DiagnosticReport.Build(Scan(bottleneck: verdict), "1.0.0", When);

            if (report.Contains("TECHNICAL ENGLISH REASON", StringComparison.Ordinal))
                offenders.Add($"{key} : aucune traduction, le motif technique anglais est rendu");
            else
            {
                var tell = EnglishTells.FirstOrDefault(t => report.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (tell is not null) offenders.Add($"{key} : mot anglais « {tell.Trim()} » dans le rendu");
            }
        }

        offenders.Should().BeEmpty(
            "le rapport est francais de bout en bout : le client le lit et le colle en public");
    }

    [Fact]
    public void The_report_builder_CANNOT_be_handed_a_license_key()
    {
        // La garde du fichier, et elle est STRUCTURELLE plutot que comportementale.
        //
        // Ecrit d'abord comme un test de contenu (« le rapport ne contient pas la cle ») : il
        // passait sans rien prouver, puisqu'il ne faisait jamais entrer la cle nulle part. Un
        // test qui ne peut pas echouer est indiscernable d'un test qui garde quelque chose.
        //
        // Ce qui protege vraiment, c'est qu'il n'existe aucun chemin par lequel une cle
        // atteindrait ce fichier : le constructeur ne prend que le scan, la version et l'instant. Ajouter un parametre de cle demain fait rougir
        // ce test — et c'est le seul moment ou quelqu'un pourrait le faire par inadvertance.
        var parameters = typeof(DiagnosticReport)
            .GetMethod(nameof(DiagnosticReport.Build))!
            .GetParameters()
            .Select(p => p.Name!)
            .ToList();

        // `sante` entre, TYPE verrouille — un RepairTriage ne porte que des
        // lectures machine (domaine, nom, valeur, explication), jamais une chaine libre.
        // `localiser` entre, TYPE verrouille — il ne fait que traduire les cles de texte
        // des constats (TitleKey, resx) ; il ne recoit ni ne rend rien que l'appelant choisisse librement.
        // `processeur` entre, TYPE verrouille (le verdict CpuDeficitSoutenu, produit par la
        // mesure) — une premiere version passait une chaine libre, refusee par cette garde a juste titre.
        // `reseau` entre, TYPE verrouillé (LigneReseau, la phrase de la mesure réseau
        // de l'écran) ; une chaîne libre a été refusée par cette garde, comme pour `processeur`.
        // `memoire` entre, TYPE verrouillé (LigneMemoireMesuree) ; même refus d'une chaîne libre.
        // `derive` entre, TYPE verrouillé (LigneDerive), même motif.
        parameters.Should().BeEquivalentTo(new[] { "scan", "appVersion", "when", "sante", "localiser", "processeur", "reseau", "memoire", "derive" });
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "derive").ParameterType.Should().Be(typeof(DrDxt.Measurement.LigneDerive));
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "memoire").ParameterType.Should().Be(typeof(DrDxt.Measurement.Memory.LigneMemoireMesuree));
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "reseau").ParameterType.Should().Be(typeof(DrDxt.Measurement.Network.LigneReseau));
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "processeur").ParameterType.Should().Be(typeof(DrDxt.Measurement.Cpu.CpuDeficitSoutenu));
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "sante").ParameterType.Should().Be(typeof(DrDxt.Core.RepairTriage));
        typeof(DiagnosticReport).GetMethod(nameof(DiagnosticReport.Build))!.GetParameters()
            .Single(p => p.Name == "localiser").ParameterType.Should().Be(typeof(Func<string, string>));
        parameters.Should().NotContain(n => n.Contains("key", StringComparison.OrdinalIgnoreCase)
                                         || n.Contains("cle", StringComparison.OrdinalIgnoreCase)
                                         || n.Contains("licen", StringComparison.OrdinalIgnoreCase));

        // Et le controle de contenu par-dessus : rien du secret ne ressort d'un rapport normal.
        DiagnosticReport.Build(Scan(), "1.0.0", When).Should().NotContain(Secret);
    }

    [Fact]
    public void The_report_carries_no_machine_identifier()
    {
        // DrDXT est gratuit : plus de licence a retrouver, donc plus d'empreinte de la
        // machine dans un fichier fait pour etre colle dans un salon public.
        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        report.Should().NotContain("Référence machine");
    }

    [Fact]
    public void A_report_without_a_scan_says_to_run_one_instead_of_looking_empty()
    {
        var report = DiagnosticReport.Build(scan: null, "1.0.0", When);

        report.Should().Contain("Aucun scan");
        report.Should().Contain("1.0.0", "la version reste utile meme sans scan : c'est la premiere question du support");
    }

    [Fact]
    public void The_machine_is_described_precisely_enough_to_be_recognised()
    {
        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        report.Should().Contain("i7-14700F");
        report.Should().Contain("RTX 4060");
        report.Should().Contain("551.86", "la version du pilote est la moitie des diagnostics GPU");
        report.Should().Contain("PRO B760-P");
        report.Should().Contain("1.H0");
        report.Should().Contain("3800", "la frequence CONFIGUREE, pas seulement la nominale");
    }

    [Fact]
    public void A_field_that_does_not_apply_is_omitted_rather_than_printed_as_zero()
    {
        // Vu sur un vrai rapport de la machine de reference : « 20c/28t, 0 CCD » sur un Intel,
        // ou la notion n'existe pas. Un champ sans objet affiche a zero se lit comme une
        // detection ratee, et fait perdre une minute au support sur un probleme inexistant.
        var intel = DiagnosticReport.Build(Scan(), "1.0.0", When);
        intel.Should().Contain("20c/28t").And.NotContain("0 CCD");

        // Et quand la notion existe, elle est dite : un X3D bi-CCD est justement l'information
        // qui explique la moitie des diagnostics d'ordonnancement.
        var amd = DiagnosticReport.Build(
            Scan() with { Hw = Hw() with { Cpu = new CpuInfo("AMD Ryzen 9 7950X3D", "AMD", 16, 32, true, 2) } },
            "1.0.0", When);
        amd.Should().Contain("2 CCD");
    }

    [Fact]
    public void Un_intitule_plus_long_que_la_colonne_garde_ses_deux_points_espaces()
    {
        // Rapport réel, « Long Duration Power Limit (PL1): Auto ».
        var lu = Scan() with
        {
            State = Scan().State with
            {
                BiosLu = new[] { new BiosSettingReading("Long Duration Power Limit (PL1)", 0, "Auto", IsDefault: true) },
            },
        };
        var rapport = DiagnosticReport.Build(lu, "1.0.0", When);
        rapport.Should().Contain("Long Duration Power Limit (PL1) : Auto");
        rapport.Should().Contain("Version               : 1.0.0", "les intitulés courts ne bougent pas");
        // Le runtime embarqué ne suit pas les correctifs de la machine ; le support doit le lire.
        rapport.Should().Contain($"Runtime .NET          : {Environment.Version}");
    }

    [Fact]
    public void La_mesure_reseau_de_l_ecran_sort_aussi_dans_le_rapport()
    {
        // L'écran affichait la gigue mesurée, le rapport rien du tout. Le réseau
        // est ce qu'un joueur ressent en premier ; le document envoyé au support ne peut pas le taire.
        const string ligne = "Réseau : mesuré, 0 ms de gigue au premier saut contre 0,78 ms sur le chemin complet.";
        DiagnosticReport.Build(Scan(), "1.0.0", When, reseau: new DrDxt.Measurement.Network.LigneReseau(ligne))
            .Should().Contain("--- Réseau mesuré ---").And.Contain(ligne);
        DiagnosticReport.Build(Scan(), "1.0.0", When)
            .Should().NotContain("--- Réseau mesuré ---", "sans mesure, pas de section vide");
    }

    [Fact]
    public void Le_1_pourcent_low_sort_avec_sa_marge()
    {
        var avec = new BenchmarkResult(140, 98.3, 80, new List<double> { 7.0 }, null, 2.1, 5, P1LowMarginOfError: 4.5);
        DiagnosticReport.Build(Scan(baseline: avec), "1.0.0", When).Should().Contain("Marge (1% low)").And.Contain("4.50");
        var sans = new BenchmarkResult(140, 98.3, 80, new List<double> { 7.0 }, null, 2.1, 5);
        DiagnosticReport.Build(Scan(baseline: sans), "1.0.0", When).Should().Contain("inconnue (moins de 3 passes)")
            .And.NotContain("Infinity");
    }

    // Le 0,1 % low, le chiffre le plus fragile du rapport (une poignée d'images par passe), sortait seul sans marge.
    [Fact]
    public void Le_0_1_pourcent_low_sort_avec_sa_marge()
    {
        var mille = Enumerable.Repeat(7.0, 1000).ToList();
        var avec = new BenchmarkResult(140, 98.3, 80, mille, null, 2.1, 5, P01LowMarginOfError: 12.3);
        DiagnosticReport.Build(Scan(baseline: avec), "1.0.0", When).Should().Contain("Marge (0.1% low)").And.Contain("12.30");
        var sans = new BenchmarkResult(140, 98.3, 80, mille, null, 2.1, 5);
        DiagnosticReport.Build(Scan(baseline: sans), "1.0.0", When).Should().Contain("Marge (0.1% low)");
    }

    // Sous mille images, le 0,1 % le plus lent n'est qu'une image : la pire. Le rapport collé en public la présentait
    // comme un 0,1 % low. Il dit combien d'images il manque.
    [Fact]
    public void Sous_mille_images_le_0_1_pourcent_low_ne_se_chiffre_pas()
    {
        var mesure = new BenchmarkResult(140, 98.3, 80, Enumerable.Repeat(7.0, 300).ToList(), null, 2.1, 5, P01LowMarginOfError: 12.3);

        var report = DiagnosticReport.Build(Scan(baseline: mesure), "1.0.0", When);

        report.Should().Contain(Docteur.T("Rapport.P01Insuffisant", 300, DiagnosticReport.ImagesPourLe01Low));
        report.Should().NotContain("80.0").And.NotContain("12.30");
    }

    [Fact]
    public void Les_queues_de_la_latence_sortent_dans_le_rapport_si_elles_meritent_leur_nom()
    {
        var trames = Enumerable.Repeat(7.0, 30).ToList();
        var mesure = new BenchmarkResult(140, 120, 100, trames, null, 1.0, 5,
            P95FrametimeMs: 9.5, AnimationErrorMsP95: 1.25, AnimationErrorSamples: 30,
            PresentStallMsP95: 0.4, PresentStallSamples: 30);

        DiagnosticReport.Build(Scan(baseline: mesure), "1.0.0", When).Should()
            .Contain("Trames lentes (p95)").And.Contain("9.50 ms")
            .And.Contain("Écart d'animation (p95)").And.Contain("1.25 ms")
            .And.Contain("Attente d'affichage (p95)").And.Contain("0.40 ms");

        // Sous vingt échantillons, un p95 se confond avec le maximum : l'écran se tait, le rapport aussi.
        var courte = mesure with { AnimationErrorSamples = 19, PresentStallSamples = 19, FrameTimesMs = trames.Take(19).ToList() };
        DiagnosticReport.Build(Scan(baseline: courte), "1.0.0", When).Should()
            .Contain("Frames capturées", "temoin : la section mesure est bien la")
            .And.NotContain("(p95)");
    }

    [Fact]
    public void Le_plafond_du_gain_de_l_ecran_sort_aussi_dans_le_rapport()
    {
        var mesure = new BenchmarkResult(200, 180, 170, new List<double> { 5.0, 5.0 }, null, 1.0, 5, MeanGpuBusyMs: 4.5);

        DiagnosticReport.Build(Scan(baseline: mesure), "1.0.0", When)
            .Should().Contain("--- Plafond du gain ---").And.Contain("11,1").And.Contain("dépend de la scène");
        DiagnosticReport.Build(Scan(), "1.0.0", When)
            .Should().NotContain("--- Plafond du gain ---", "sans mesure, pas de section vide");
    }

    [Fact]
    public void La_derive_de_l_ecran_sort_aussi_dans_le_rapport()
    {
        const string ligne = "Depuis le 11/09, ce jeu tourne 18 % plus lentement sur cette machine (au-delà du bruit de 3 %).";
        DiagnosticReport.Build(Scan(), "1.0.0", When, derive: new DrDxt.Measurement.LigneDerive(ligne))
            .Should().Contain("--- Dérive des performances ---").And.Contain(ligne);
        DiagnosticReport.Build(Scan(), "1.0.0", When)
            .Should().NotContain("--- Dérive des performances ---", "sans comparaison, pas de section vide");
    }

    [Fact]
    public void La_mesure_memoire_de_l_ecran_sort_aussi_dans_le_rapport()
    {
        const string ligne = "Mémoire mesurée le 16/09/2026 : 12,7 Go/s, 128,5 ns. Prise dans le même scan qu'une capture de cod.";
        DiagnosticReport.Build(Scan(), "1.0.0", When, memoire: new DrDxt.Measurement.Memory.LigneMemoireMesuree(ligne))
            .Should().Contain("--- Mémoire mesurée ---").And.Contain(ligne);
        DiagnosticReport.Build(Scan(), "1.0.0", When)
            .Should().NotContain("--- Mémoire mesurée ---", "sans mesure, pas de section vide");
    }

    [Fact]
    public void The_register_and_the_per_lever_detail_are_both_there()
    {
        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        report.Should().MatchRegex(@"Leviers à corriger\s*: 1\r?\n");
        report.Should().Contain("2 sur 3", "la couverture dit ce qui n'a PAS ete lu");
        // Les etats sont dits en francais : le client lit ce fichier. Chaque levier apparait avec
        // son etat, et les deux etats
        // differents restent distinguables.
        report.Should().Contain("xmp").And.Contain("à corriger");
        report.Should().Contain("x3d-ccd").And.Contain("non lu");
    }

    [Fact]
    public void The_bottleneck_verdict_carries_its_REASON_not_just_its_code()
    {
        // Un etat sans son motif est un code, pas un diagnostic : le support ne pourrait pas
        // comprendre le verdict sans rejouer le scan.
        var verdict = new BottleneckVerdict(
            BottleneckState.GpuBound, BottleneckConfidence.High,
            "The GPU paced 96% of each frame and barely waited.", Provisional: false);

        var report = DiagnosticReport.Build(Scan(bottleneck: verdict), "1.0.0", When);

        report.Should().Contain("limité par le GPU");
        report.Should().Contain("barely waited");
    }

    [Fact]
    public void A_missing_measurement_is_SAID_not_shown_as_zero()
    {
        // Une mesure absente n'est pas une mesure a zero : le dire evite au support de chercher
        // pourquoi les chiffres sont mauvais alors qu'il n'y en a pas.
        var report = DiagnosticReport.Build(Scan(baseline: null), "1.0.0", When);

        report.Should().Contain("Aucune mesure");
        report.Should().NotContain("FPS moyen           : 0");
    }

    [Fact]
    public void A_measurement_never_travels_without_its_margin_and_its_run_count()
    {
        // Un chiffre de FPS sans sa marge et son nombre de passes ne veut rien dire — c'est la
        // regle de mesure du produit, et le rapport ne doit pas la contourner.
        var baseline = new BenchmarkResult(138.9, 120.0, 90.0,
            new List<double> { 7.0, 7.1 }, null, 2.35, 5);

        var report = DiagnosticReport.Build(Scan(baseline: baseline), "1.0.0", When);

        report.Should().Contain("138.9");
        report.Should().Contain("2.35", "la marge d'erreur voyage avec le chiffre");
        report.Should().Contain("Passes").And.Contain("5");
    }

    [Fact]
    public void Numbers_use_an_invariant_format_so_a_pasted_report_reads_the_same_everywhere()
    {
        // Un rapport colle dans un ticket est lu par quelqu'un dont la machine n'a pas la meme
        // culture. « 138,9 » chez l'un et « 138.9 » chez l'autre rend les comparaisons penibles.
        var baseline = new BenchmarkResult(138.9, 120.0, 90.0, new List<double> { 7.0 }, null, 2.35, 5);

        var report = DiagnosticReport.Build(Scan(baseline: baseline), "1.0.0", When);

        report.Should().Contain("138.9").And.NotContain("138,9");
    }

    // Sous trois passes la marge vaut +inf ; le rapport ecrivait « Infinity ».
    [Fact]
    public void Une_marge_infinie_se_dit_inconnue_dans_le_rapport()
    {
        var baseline = new BenchmarkResult(138.9, 120.0, 90.0, new List<double> { 7.0 }, null, double.PositiveInfinity, 1);

        var report = DiagnosticReport.Build(Scan(baseline: baseline), "1.0.0", When);

        report.Should().Contain("inconnue (moins de 3 passes)").And.NotContain("Infinity").And.NotContain("∞");
    }

    [Fact]
    public void The_file_name_carries_the_date_so_two_reports_do_not_overwrite_each_other()
    {
        // Deux rapports du meme client a deux moments racontent une histoire que le second seul
        // ne raconte pas.
        DiagnosticReport.FileNameFor(When).Should().Be("drdxt-diagnostic-2026-09-02-0730.txt");
        DiagnosticReport.FileNameFor(When.AddHours(3))
            .Should().NotBe(DiagnosticReport.FileNameFor(When));
    }

    [Fact]
    public void The_coverage_matrix_travels_with_the_report()
    {
        // Le support doit savoir ce que cette machine RECOIT avant de chercher pourquoi un
        // levier n'a rien fait.
        var report = DiagnosticReport.Build(Scan(), "1.0.0", When);

        // La carte dit son état : la RTX 4060 (Ada) a son rapport archivé.
        report.Should().Contain("RTX 4060").And.Contain("réglable, validé sur cette architecture");
    }

    [Fact]
    public void The_report_uses_the_same_memory_unit_as_the_screen()
    {
        // Une source, deux surfaces : le rapport dit MT/s comme l'ecran (3800 MT/s = 1900 MHz reels).
        // C'est le rapport que le client colle dans son ticket, donc celui qui voyage et qu'on relit
        // six mois plus tard.
        var report = DiagnosticReport.Build(Scan(), appVersion: "1.0.0", when: When);

        report.Should().NotContain(" MHz", "l'etiquette MHz est fausse pour une DDR : 3800 MT/s = 1900 MHz");
        report.Should().Contain(" MT/s", "le rapport dit la meme chose que l'ecran, ou il ne dit rien de sur");
    }
}
