using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// LM — le cablage du banc memoire. Sans lui, MemoryBench serait un instrument que le produit
// n'appelle jamais : le defaut exact que ce lot vient reparer, et que ce depot a deja paye avec
// AbComparer et avec le verifieur de boucle BIOS.
//
// Ce que ces tests prouvent, et qu'aucune lecture de code ne prouve : le scan LANCE la mesure,
// il passe le resultat au moteur de findings, et il ne la relance pas au scan suivant.
// -----------------------------------------------------------------------
public sealed class ScanMeasuresMemoryTests
{
    private sealed class BancFake : IMemoryBench
    {
        private readonly double _debit;
        private readonly double _sigma;
        private readonly int _bruyantsDAbord;
        public int Lancements;
        public readonly List<int> FilsAppelants = new();

        public BancFake(double debitGBs = 34.20, double sigma = 3.34, int bruyantsDAbord = 0)
        {
            _debit = debitGBs;
            _sigma = sigma;
            _bruyantsDAbord = bruyantsDAbord;
        }

        public MemoryMeasurement Measure()
        {
            Lancements++;
            FilsAppelants.Add(Environment.CurrentManagedThreadId);
            // Les N premieres mesures sont trop dispersees (machine occupee), puis ca se calme.
            if (Lancements <= _bruyantsDAbord)
                return new MemoryMeasurement(_debit, 9.0, 71.33, 4.53,
                    new[] { new StaircasePoint(32 * 1024, 0.95), new StaircasePoint(128 * 1024 * 1024, 75.33) });
            return new MemoryMeasurement(_debit, _sigma, 71.33, 4.53,
                new[]
                {
                    new StaircasePoint(32 * 1024, 0.95),
                    new StaircasePoint(128 * 1024 * 1024, 75.33),
                });
        }
    }

    private sealed class VoyageFake : DrDxt.App.Platform.IFirmwareTrip
    {
        private readonly BiosLoopResult _verdict;
        public VoyageFake(BiosLoopResult verdict) => _verdict = verdict;

        /// <summary>Combien de fois le banc avait tourne au moment ou le voyage a ete arme.</summary>
        public readonly List<int> BancAuDepart = new();
        public Func<int>? LancementsDuBanc;

        public Task<Result> ArmAndRebootAsync(
            IReadOnlyList<BiosFinding> findings, SettingsState before, int delaySeconds, CancellationToken ct)
        {
            BancAuDepart.Add(LancementsDuBanc?.Invoke() ?? -1);
            return Task.FromResult(Result.Ok());
        }

        public Result<BiosLoopResult> VerifyOnReturn(SettingsState fresh)
            => Result<BiosLoopResult>.Ok(_verdict);

        public int Oublis;
        public Result ResultatOubli { get; set; } = Result.Ok();
        public Result Oublier() { Oublis++; return ResultatOubli; }
    }

    private sealed class FichierFake : IMemoryBaselineFile
    {
        private string? _contenu;
        public Result WriteAtomic(string json) { _contenu = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(_contenu);
    }

    // Le debit enregistre, lu dans le JSON : chercher « 12.7 » dans le texte trouvait aussi l'horodatage
    // (« 17:00:12.78 ») et faisait echouer le test une fois sur quelques centaines.
    private static double Debit(FichierFake fichier) => System.Text.Json.JsonDocument.Parse(fichier.Read().Value!)
        .RootElement.GetProperty("Measurement").GetProperty("BandwidthGBs").GetDouble();

    private static HardwareInventory Hw(int mts = 3800) => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[]
        {
            new MemoryModule("PN", "Kingston", mts, 4000),
            new MemoryModule("PN", "Kingston", mts, 4000),
        },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static (ScanViewModel vm, BancFake banc, IScanScorer scorer) Build(
        DrDxt.App.Platform.IFirmwareTrip? voyage = null, IMemoryBaselineFile? fichier = null,
        int mts = 3800, double debitGBs = 34.20, double sigma = 3.34, Func<string>? machineId = null,
        int bruyantsDAbord = 0, IBiosAdvisor? conseiller = null, IGuideGenerator? guide = null, int? jeu = null,
        DrDxt.App.Platform.IGameLocator? locator = null, DrDxt.Measurement.PerformanceJournalStore? journal = null,
        Func<string, string, string>? writeReport = null)
    {
        if (locator is null)
        {
            locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
            locator.LocateRunningGamePid().Returns(jeu);
            locator.JeuOuvert().Returns(jeu is not null);
        }

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw(mts)));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), 68, bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        var banc = new BancFake(debitGBs, sigma, bruyantsDAbord);
        var service = new MemoryBaselineService(
            banc, new MemoryBaselineStore(fichier ?? new FichierFake()), () => DateTimeOffset.UtcNow);

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), ScanViewModelTestSeams.GpuTuningStep(),
            ScanViewModelTestSeams.Navigation(), ScanViewModelTestSeams.DashboardFactory(),
            store, new AppliedTweakManifestMapper(),
            conseiller ?? ScanViewModelTestSeams.BiosAdvisor(),
            guide ?? ScanViewModelTestSeams.GuideGenerator(), key => key,
            gameLocator: locator, firmwareTrip: voyage,
            memoryBaseline: service, machineId: machineId ?? (() => "MACHINE-A"), performanceJournal: journal,
            writeReport: writeReport);

        return (vm, banc, sc);
    }

    [Fact]
    public async Task Le_premier_scan_LANCE_le_banc_memoire()
    {
        var (vm, banc, _) = Build();

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public async Task Le_scan_passe_les_canaux_PROUVES_au_moteur_de_findings()
    {
        // 34,20 Go/s a 3800 MT/s depassent le plafond d'un canal (30,4) : deux canaux prouves.
        // Si ce chiffre n'arrivait pas jusqu'au scorer, la mesure ne changerait rien a l'ecran.
        var (vm, _, scorer) = Build();

        await vm.ScanCommand.ExecuteAsync(null);

        scorer.Received(1).Score(
            Arg.Any<HardwareInventory>(),
            Arg.Is<SettingsState>(s => s.MemoryProvenChannels == 2),
            Arg.Any<BenchmarkResult?>());
    }

    [Fact]
    public async Task Le_deuxieme_scan_ne_remesure_pas()
    {
        var (vm, banc, _) = Build();

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public async Task Sans_voyage_BIOS_aucune_section_de_preuve_et_aucune_remesure()
    {
        // Deux scans ordinaires : le banc ne tourne qu'une fois (la reference), et la section
        // n'existe pas. Une section vide qui s'affiche est un defaut, pas une precaution.
        var (vm, banc, _) = Build();

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasMemoryAb.Should().BeFalse();
        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public async Task Au_retour_d_un_voyage_BIOS_la_section_montre_l_avant_et_l_apres()
    {
        // Une reference existe deja (premier scan), puis le client revient du BIOS.
        var fichier = new FichierFake();
        var premier = Build(fichier: fichier);
        await premier.vm.ScanCommand.ExecuteAsync(null);

        var voyage = new VoyageFake(new BiosLoopResult(
            HadPendingChange: true, AllTook: true,
            Outcomes: new[]
            {
                new BiosSettingOutcome(
                    FirmwareSettingKind.XmpExpo, TriState.On, TriState.On, BiosSettingVerdict.Took),
            },
            ShowRecoveryCard: false, RecoveryCardKeys: Array.Empty<string>(), MarkerCleared: true));

        var (vm, _, _) = Build(voyage: voyage, fichier: fichier);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosLoopResult.Should().BeTrue();
        vm.HasMemoryAb.Should().BeTrue();
        vm.MemoryAb!.Lines.Should().HaveCount(2);
        vm.MemoryAb.Lines[0].Before.Should().NotBeNullOrWhiteSpace();
        vm.MemoryAb.Lines[0].After.Should().NotBeNullOrWhiteSpace();
    }

    private static VoyageFake VoyageReussi() => new(new BiosLoopResult(
        HadPendingChange: true, AllTook: true,
        Outcomes: new[]
        {
            new BiosSettingOutcome(
                FirmwareSettingKind.XmpExpo, TriState.On, TriState.On, BiosSettingVerdict.Took),
        },
        ShowRecoveryCard: false, RecoveryCardKeys: Array.Empty<string>(), MarkerCleared: true));

    [Fact]
    public async Task Un_A_B_REFUSE_ne_detruit_pas_la_reference_qu_il_faudra_recomparer()
    {
        // Le defaut le plus cher de la revue. Quand l'A/B ne tranche pas (machine occupee),
        // l'ecran dit au client « relance au repos » -- mais le rafraichissement qui suit, dans le
        // Meme scan, voyait la frequence changee, remesurait et ECRASAIT la reference d'avant.
        // Le marqueur BIOS ayant deja ete consomme, la comparaison devenait impossible A jamais :
        // le produit donnait un conseil qu'il venait lui-meme de rendre inapplicable.
        var fichier = new FichierFake();
        var avant = Build(fichier: fichier, mts: 2133, debitGBs: 19.1);
        await avant.vm.ScanCommand.ExecuteAsync(null);

        // Retour du BIOS. La machine s'agite pendant les trois essais de l'A/B (Q-03), puis se
        // calme -- et c'est exactement la que le rafraichissement ecrasait la reference.
        var (vm, banc, _) = Build(voyage: VoyageReussi(), fichier: fichier, mts: 3800,
                                  debitGBs: 34.2, bruyantsDAbord: 3);
        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(3, "les trois essais de l'A/B, et RIEN apres");

        vm.HasMemoryAb.Should().BeTrue("l'ecran doit expliquer pourquoi il ne tranche pas");
        new MemoryBaselineStore(fichier).Read("MACHINE-A").Value!.ConfiguredMts
            .Should().Be(2133, "la reference d'AVANT le BIOS doit survivre a un A/B refuse");
    }

    [Fact]
    public async Task Le_banc_ne_tourne_JAMAIS_sur_le_fil_qui_pilote_l_ecran()
    {
        // Le banc alloue et parcourt 384 Mo, et reessaie jusqu'a trois fois : sur le fil de
        // l'interface, c'est une fenetre figee jusqu'a une minute et demie. Le bloc voisin le
        // savait deja (« parcourir 384 Mo bloquerait l'interface ») ; le chemin de l'A/B, non.
        // Une reference doit exister, sinon l'A/B rend null sans jamais mesurer et le test
        // passerait sans rien prouver.
        var fichier = new FichierFake();
        var premier = Build(fichier: fichier, mts: 2133, debitGBs: 19.1);
        await premier.vm.ScanCommand.ExecuteAsync(null);

        var (vm, banc, _) = Build(voyage: VoyageReussi(), fichier: fichier, mts: 3800, debitGBs: 34.2);
        var filDuTest = Environment.CurrentManagedThreadId;

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().BeGreaterThan(0, "sans mesure, ce test ne prouverait rien");
        banc.FilsAppelants.Should().NotContain(filDuTest);
    }

    [Fact]
    public async Task Une_panne_du_cote_memoire_ne_jette_PAS_le_verdict_BIOS_deja_calcule()
    {
        // Le verdict du voyage BIOS est calcule avant la mesure memoire. Une exception du cote
        // memoire ne doit pas emporter un verdict deja obtenu : le client perdrait la seule chose
        // qui lui disait si son changement a pris.
        var (vm, _, _) = Build(voyage: VoyageReussi(),
                               machineId: () => throw new InvalidOperationException("identite illisible"));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosLoopResult.Should().BeTrue("le verdict BIOS etait deja calcule");
        vm.HasMemoryAb.Should().BeFalse("seule la moitie memoire disparait");
    }

    [Fact]
    public async Task La_reference_d_AVANT_le_BIOS_survit_au_scan_qui_la_relit()
    {
        // Le defaut que ce test ferme : le scan rafraichissait la reference avant de rendre le
        // verdict de retour BIOS. Or un passage XMP off -> on change la frequence lue, donc la
        // reference etait jugee caduque, REMESUREE et ECRASEE -- et l'A/B comparait ensuite
        // l'apres a l'apres. Resultat : « aucune difference mesurable » annonce sur un vrai +79 %,
        // c'est-a-dire l'exact contraire de ce que le produit promet.
        var fichier = new FichierFake();

        // Avant le BIOS : 2133 MT/s, 19,1 Go/s. C'est la reference a preserver.
        var avant = Build(fichier: fichier, mts: 2133, debitGBs: 19.1);
        await avant.vm.ScanCommand.ExecuteAsync(null);

        // Retour du BIOS : la machine tourne a 3800 et rend 34,2 Go/s.
        var voyage = new VoyageFake(new BiosLoopResult(
            HadPendingChange: true, AllTook: true,
            Outcomes: new[]
            {
                new BiosSettingOutcome(
                    FirmwareSettingKind.XmpExpo, TriState.On, TriState.On, BiosSettingVerdict.Took),
            },
            ShowRecoveryCard: false, RecoveryCardKeys: Array.Empty<string>(), MarkerCleared: true));

        var (vm, _, _) = Build(voyage: voyage, fichier: fichier, mts: 3800, debitGBs: 34.2);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasMemoryAb.Should().BeTrue();
        vm.MemoryAb!.Lines[0].IsSignificant.Should().BeTrue(
            "un gain de 79 % ecrase le bruit : si le test echoue ici, la reference d'avant a ete perdue");
        vm.MemoryAb.Lines[0].Delta.Should().Contain("79");
        vm.MemoryAb.Conclusion.Should().Be(MemoryAbStringKeys.Significant);
    }

    // ── Et les deux chiffres arrivent a l'ecran ────────────────────────────────────────────
    //
    // Le scan payait trente a quarante secondes de banc, enregistrait le resultat, et aucune
    // couche visible ne lisait le debit ni la latence : ils ne servaient qu'a deduire un nombre de
    // canaux. Mesures, puis jetes.

    [Fact]
    public async Task Le_scan_MONTRE_ce_que_la_memoire_rend_vraiment()
    {
        var (vm, banc, _) = Build(debitGBs: 34.20, sigma: 3.34);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(1);
        vm.MemoryMeasuredLabel.Should().Be("Scan.Memory.Measured",
            "la localisation factice rend la cle ; le texte est verrouille par ScanLocalizationTests");
    }

    [Fact]
    public async Task Une_mesure_REJETEE_n_affiche_aucun_chiffre()
    {
        // L'invariant, plutot qu'une valeur : le magasin ne conserve pas ce que les controles
        // ont refuse, donc l'ecran ne peut pas publier un chiffre rejete. Ici la dispersion depasse
        // le maximum accepte (Q-03) a chacune des trois tentatives.
        var (vm, banc, _) = Build(sigma: 9.0);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().BeGreaterThan(1, "le service reessaie avant d'abandonner");
        vm.MemoryMeasuredLabel.Should().BeNull("« refuse » ne se raconte pas comme « mesure »");
    }

    [Fact]
    public async Task Une_reference_PERIMEE_ne_s_affiche_pas()
    {
        // Le piege propre a ce magasin. Il garde DELIBEREMENT la mesure d'avant quand la
        // configuration a change : c'est elle qui rend l'A/B possible apres un passage dans le
        // BIOS. Si la remesure echoue ensuite -- ici une machine trop agitee, rejet Q-03 -- la
        // reference d'avant reste seule en place. L'afficher annoncerait comme « ce que ta memoire
        // rend » un chiffre pris dans une configuration que la machine a quittee.
        var fichier = new FichierFake();
        var (premier, _, _) = Build(fichier: fichier, mts: 3800);
        await premier.ScanCommand.ExecuteAsync(null);
        premier.MemoryMeasuredLabel.Should().NotBeNull("la mesure d'origine, elle, est affichable");

        // La memoire tourne maintenant a une autre vitesse, et le banc ne rend rien d'acceptable.
        var (apres, _, _) = Build(fichier: fichier, mts: 2133, sigma: 9.0);
        await apres.ScanCommand.ExecuteAsync(null);

        apres.MemoryMeasuredLabel.Should().BeNull(
            "la reference d'avant decrit une machine qui n'existe plus ; se taire est le seul "
            + "resultat honnete tant qu'une mesure de la configuration COURANTE n'existe pas");
    }

    [Fact]
    public async Task Le_scan_suivant_AFFICHE_sans_relancer_le_banc()
    {
        // Afficher un chiffre ne doit pas couter quarante secondes de banc a chaque ouverture.
        var fichier = new FichierFake();
        var (premier, _, _) = Build(fichier: fichier);
        await premier.ScanCommand.ExecuteAsync(null);

        var (second, bancDuSecond, _) = Build(fichier: fichier);
        await second.ScanCommand.ExecuteAsync(null);

        bancDuSecond.Lancements.Should().Be(0);
        second.MemoryMeasuredLabel.Should().NotBeNull("relire suffit a afficher");
    }

    // 26/09/2026 : sur la machine de reference, la reference memoire du 16/09 rendait 12,7 Go/s et
    // 128,5 ns, la meme machine 33,4 Go/s et 74 ns dix jours plus tard. Comparee a elle, un passage
    // au profil 4 000 aurait ete annonce a +160 %. Au retour, une mesure d'avant qui ne date pas du
    // depart ne donne AUCUN chiffre.
    [Fact]
    public async Task Au_retour_une_mesure_d_avant_d_un_autre_jour_ne_donne_aucun_chiffre()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier).vm.ScanCommand.ExecuteAsync(null);

        var voyage = new VoyageFake(new BiosLoopResult(
            HadPendingChange: true, AllTook: true,
            Outcomes: new[]
            {
                new BiosSettingOutcome(
                    FirmwareSettingKind.XmpExpo, TriState.On, TriState.On, BiosSettingVerdict.Took),
            },
            ShowRecoveryCard: false, RecoveryCardKeys: Array.Empty<string>(), MarkerCleared: true,
            DepartUtc: DateTimeOffset.UtcNow.AddDays(2)));

        var (vm, _, _) = Build(voyage: voyage, fichier: fichier);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasMemoryAb.Should().BeTrue();
        vm.MemoryAb!.Lines.Should().BeEmpty();
        vm.MemoryAb.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictStale);
    }

    // 26/09/2026 : au retour, un jeu lance ferait lire son propre ecart comme celui du changement.
    [Fact]
    public async Task Au_retour_jeu_lance_l_A_B_ne_mesure_pas_et_le_dit()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier).vm.ScanCommand.ExecuteAsync(null);

        var (vm, banc, _) = Build(voyage: VoyageReussi(), fichier: fichier, jeu: 4242);
        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(0);
        vm.HasMemoryAb.Should().BeTrue("l'ecran doit dire pourquoi il ne compare pas");
        vm.MemoryAb!.Lines.Should().BeEmpty();
        vm.MemoryAb.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictGame);
    }

    [Fact]
    public async Task Partir_vers_le_BIOS_jeu_lance_ne_mesure_pas_la_memoire()
    {
        var voyage = new VoyageFake(BiosLoopResult.NothingPending);
        var constat = new BiosFinding(FirmwareSettingKind.XmpExpo, Severity.Major, "XmpExpo.Title", "XmpExpo.Rationale",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "XmpExpo.Action",
            Gain: null, StateNotReadable: false);
        var guide = ScanViewModelTestSeams.Guide(GuideConfidence.Verified, false, "Carte",
            ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp", "Ai Tweaker", 1));
        var (vm, banc, _) = Build(voyage: voyage, jeu: 4242,
            conseiller: ScanViewModelTestSeams.BiosAdvisor(constat), guide: ScanViewModelTestSeams.GuideGenerator(guide));
        voyage.LancementsDuBanc = () => banc.Lancements;
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide.Should().NotBeNull("temoin : sans guide, le bouton n'existe pas et ce test ne prouverait rien");
        (await vm.BiosGuide!.OpenFirmwareSetupAsync()).IsOk.Should().BeTrue();

        voyage.BancAuDepart.Should().ContainSingle("le voyage part quand meme").Which.Should().Be(0);
    }

    [Fact]
    public async Task Partir_vers_le_BIOS_mesure_la_memoire_AVANT_d_armer_le_voyage()
    {
        var voyage = new VoyageFake(BiosLoopResult.NothingPending);
        var constat = new BiosFinding(FirmwareSettingKind.XmpExpo, Severity.Major, "XmpExpo.Title", "XmpExpo.Rationale",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "XmpExpo.Action",
            Gain: null, StateNotReadable: false);
        var guide = ScanViewModelTestSeams.Guide(GuideConfidence.Verified, false, "Carte",
            ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp", "Ai Tweaker", 1));
        var (vm, banc, _) = Build(voyage: voyage,
            conseiller: ScanViewModelTestSeams.BiosAdvisor(constat), guide: ScanViewModelTestSeams.GuideGenerator(guide));
        voyage.LancementsDuBanc = () => banc.Lancements;
        await vm.ScanCommand.ExecuteAsync(null);
        var avantLeClic = banc.Lancements;

        vm.BiosGuide.Should().NotBeNull("temoin : sans guide, le bouton n'existe pas et ce test ne prouverait rien");
        (await vm.BiosGuide!.OpenFirmwareSetupAsync()).IsOk.Should().BeTrue();

        voyage.BancAuDepart.Should().ContainSingle()
            .Which.Should().BeGreaterThan(avantLeClic, "la mesure d'avant est prise avant d'armer le voyage");
    }

    // 26/09/2026 : a configuration inchangee, la reference ne se remesurait jamais. Sur la machine de
    // reference, celle du 16/09 (12,7 Go/s) restait affichee quand la memoire rendait 33,4 Go/s.
    [Fact]
    public async Task Remesurer_la_memoire_remplace_la_reference_affichee()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier, debitGBs: 12.7).vm.ScanCommand.ExecuteAsync(null);
        var (vm, banc, _) = Build(fichier: fichier, debitGBs: 33.4);
        await vm.ScanCommand.ExecuteAsync(null);
        banc.Lancements.Should().Be(0, "configuration inchangee : le scan relit, il ne remesure pas");
        vm.RemesureMemoireVisible.Should().BeTrue();
        Debit(fichier).Should().Be(12.7);

        await vm.RemesurerMemoireCommand.ExecuteAsync(null);

        banc.Lancements.Should().BeGreaterThan(0);
        Debit(fichier).Should().Be(33.4);
        vm.RemesureMemoireFaite.Should().BeTrue();
        vm.ErreurRemesureMemoire.Should().BeNull();
    }

    // 26/09/2026 : la reference du 16/09 a ete prise pendant que Warzone tournait (scan de 17:53,
    // capture du jeu a 17:54). Ni le scan ni le bouton ne mesurent jeu lance.
    [Fact]
    public async Task Un_scan_jeu_lance_ne_lance_pas_le_banc_memoire()
    {
        var fichier = new FichierFake();
        var (vm, banc, _) = Build(fichier: fichier, jeu: 4242);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(0);
        fichier.Read().Value.Should().BeNullOrEmpty();
    }

    // Alt+Tab sur un plein ecran exclusif le reduit : aucun jeu a capturer, mais un jeu qui tourne.
    [Fact]
    public async Task Un_jeu_reduit_suffit_a_ne_pas_lancer_le_banc_memoire()
    {
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(true);
        var (vm, banc, _) = Build(locator: locator);
        vm.Patienter = _ => Task.CompletedTask; // le scan attend que le jeu revienne : pas ici

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(0);
        vm.MemoireSauteeJeu.Should().BeTrue();
        vm.ErreurRemesureMemoire.Should().BeNull("une information, pas une erreur");
    }

    // Jeu fermé, le joueur remesure depuis la carte : « pas mesurée à ce scan » ne doit pas rester sous la
    // mesure qu'il vient d'obtenir.
    [Fact]
    public async Task Une_remesure_reussie_efface_le_motif_du_scan_saute()
    {
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242);
        locator.JeuOuvert().Returns(true);
        var (vm, _, _) = Build(locator: locator);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.MemoireSauteeJeu.Should().BeTrue("temoin : le scan a saute la mesure");

        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(false);
        await vm.RemesurerMemoireCommand.ExecuteAsync(null);

        vm.RemesureMemoireFaite.Should().BeTrue();
        vm.MemoireSauteeJeu.Should().BeFalse();
    }

    // 26/09/2026 : une référence prise avant le garde « jeu lancé » (celle du 16/09 : capture de Warzone 1 min
    // 40 plus tard, même scan) est relue telle quelle. La ligne le dit, sur la foi du journal, sans seuil inventé.
    private sealed class JournalFake : DrDxt.Measurement.IPerformanceJournalFile
    {
        private string? _contenu;
        public Result WriteAtomic(string json) { _contenu = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(_contenu);
    }

    [Theory]
    [InlineData(100, "MACHINE-A", true)]    // le cas du 16/09 : même scan
    [InlineData(-240, "MACHINE-A", true)]   // capture avant la mesure, même scan
    [InlineData(900, "MACHINE-A", false)]   // quinze minutes : un autre moment
    [InlineData(100, "MACHINE-B", false)]   // une autre machine ne dit rien de celle-ci
    public void Une_capture_de_jeu_du_meme_scan_est_retrouvee(int ecartSecondes, string machine, bool attendue)
    {
        var reference = new DateTimeOffset(2026, 9, 16, 17, 53, 12, TimeSpan.Zero);
        var journal = new[]
        {
            new DrDxt.Core.PerformanceSample(reference.AddSeconds(ecartSecondes), machine, 135.8, 2, 5, 1.0, "cod"),
        };

        var trouvee = ScanViewModel.CaptureDuMemeScan(journal, reference, "MACHINE-A");

        (trouvee is not null).Should().Be(attendue);
    }

    // Une référence écrite avant le garde n'a pas le champ : la retirer reproduit le fichier de la machine de référence.
    private static void CommeAvantLeGarde(FichierFake fichier)
    {
        var n = System.Text.Json.Nodes.JsonNode.Parse(fichier.Read().Value!)!.AsObject();
        n.Remove("JeuFermeVerifie").Should().BeTrue("temoin : cette version marque ses mesures");
        fichier.WriteAtomic(n.ToJsonString());
    }

    [Fact]
    public async Task La_ligne_memoire_dit_quand_la_reference_a_ete_prise_pendant_une_partie()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier).vm.ScanCommand.ExecuteAsync(null);
        CommeAvantLeGarde(fichier);
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalFake());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow, "MACHINE-A", 135.8, 2, 5, 1.0, "cod"));
        var (vm, _, _) = Build(fichier: fichier, journal: store);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.MemoryMeasuredLabel.Should().Contain("Scan.Memory.PriseEnJeu");
    }

    // Le rapport est fait pour être envoyé : la ligne mémoire y sort avec sa réserve, comme à l'écran.
    [Fact]
    public async Task Le_rapport_porte_la_ligne_memoire_et_sa_reserve()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier).vm.ScanCommand.ExecuteAsync(null);
        CommeAvantLeGarde(fichier);
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalFake());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow, "MACHINE-A", 135.8, 2, 5, 1.0, "cod"));
        string? ecrit = null;
        var (vm, _, _) = Build(fichier: fichier, journal: store, writeReport: (_, texte) => { ecrit = texte; return "r.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().Contain("--- Mémoire mesurée ---").And.Contain("Scan.Memory.PriseEnJeu");
    }

    // Une référence prise sous le garde, jeu fermé, puis un jeu lancé et capturé cinq minutes plus tard : rien
    // n'a été faussé, la ligne ne soupçonne pas la mesure.
    [Fact]
    public async Task Une_reference_prise_sous_le_garde_ne_se_soupconne_pas()
    {
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalFake());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow.AddMinutes(5), "MACHINE-A", 135.8, 2, 5, 1.0, "cod"));
        var (vm, banc, _) = Build(journal: store);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().BeGreaterThan(0, "temoin : la reference vient d'etre mesuree");
        vm.MemoryMeasuredLabel.Should().NotBeNull().And.NotContain("Scan.Memory.PriseEnJeu");
    }

    [Fact]
    public async Task Sans_capture_de_jeu_proche_la_ligne_memoire_ne_dit_rien_de_plus()
    {
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalFake());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow.AddHours(-3), "MACHINE-A", 135.8, 2, 5, 1.0, "cod"));
        var (vm, _, _) = Build(journal: store);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.MemoryMeasuredLabel.Should().NotBeNull("temoin : la ligne existe")
            .And.NotContain("Scan.Memory.PriseEnJeu");
    }

    [Fact]
    public async Task Remesurer_jeu_lance_est_refuse_et_dit_de_fermer_le_jeu()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier, debitGBs: 34.2).vm.ScanCommand.ExecuteAsync(null);
        var (vm, banc, _) = Build(fichier: fichier, debitGBs: 12.7, jeu: 4242);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.RemesureMemoireVisible.Should().BeTrue("temoin : sans carte, pas de bouton a refuser");

        await vm.RemesurerMemoireCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(0);
        Debit(fichier).Should().Be(34.2);
        vm.ErreurRemesureMemoire.Should().Be("Memoire.Remesure.JeuEnCours");
        vm.RemesureMemoireFaite.Should().BeFalse();
    }

    [Fact]
    public async Task Une_remesure_refusee_garde_l_ancienne_et_le_dit()
    {
        var fichier = new FichierFake();
        await Build(fichier: fichier, debitGBs: 12.7).vm.ScanCommand.ExecuteAsync(null);
        var (vm, _, _) = Build(fichier: fichier, debitGBs: 33.4, bruyantsDAbord: 99);
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.RemesurerMemoireCommand.ExecuteAsync(null);

        Debit(fichier).Should().Be(12.7, "une mesure refusee ne remplace rien");
        vm.ErreurRemesureMemoire.Should().Be("Memoire.Remesure.Echec");
        vm.RemesureMemoireFaite.Should().BeFalse();
    }

    // 26/09/2026 : la carte existe a l'ecran, dans le domaine memoire, et seulement quand une mesure est
    // affichee. Rendue sur la vraie ScanView apres un vrai scan (le domaine est construit par le scan).
    [Fact]
    public async Task La_carte_de_remesure_s_affiche_dans_le_domaine_memoire()
    {
        var (vm, _, _) = Build();
        await vm.ScanCommand.ExecuteAsync(null);
        vm.RemesureMemoireVisible.Should().BeTrue("temoin : le scan a pose une mesure");

        var textes = TextesDuDomaineMemoire(vm);

        textes.Should().Contain("Memoire.Remesure.Titre");
        textes.Should().Contain("Memoire.Remesure.Description");
        textes.Should().NotContain("Memoire.Remesure.SauteeJeu", "rien n'a été sauté : pas de motif");
    }

    // 26/09/2026 : le scan capture le jeu, donc le parcours normal le lance avant de scanner. Sans mesure
    // faute de jeu ferme, la carte doit quand meme etre la, dire pourquoi, et offrir le bouton.
    [Fact]
    public async Task Jeu_lance_sans_reference_la_carte_dit_pourquoi_et_offre_le_bouton()
    {
        var fichier = new FichierFake();
        var (vm, banc, _) = Build(fichier: fichier, jeu: 4242);
        await vm.ScanCommand.ExecuteAsync(null);
        banc.Lancements.Should().Be(0, "temoin : la mesure a bien ete sautee");
        vm.MemoryMeasuredLabel.Should().BeNull();

        vm.RemesureMemoireVisible.Should().BeTrue();
        var textes = TextesDuDomaineMemoire(vm);
        textes.Should().Contain("Memoire.Remesure.Titre");
        textes.Should().Contain("Memoire.Remesure.SauteeJeu");
    }

    [Fact]
    public async Task Un_scan_sans_jeu_efface_la_raison_du_scan_precedent()
    {
        // Le meme ecran, deux scans : le joueur ferme le jeu entre les deux.
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242);
        locator.JeuOuvert().Returns(true);
        var (vm, _, _) = Build(locator: locator);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.MemoireSauteeJeu.Should().BeTrue("temoin : le premier scan a saute");

        locator.LocateRunningGamePid().Returns((int?)null);
        locator.JeuOuvert().Returns(false);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.MemoireSauteeJeu.Should().BeFalse();
        vm.ErreurRemesureMemoire.Should().BeNull();
        vm.MemoryMeasuredLabel.Should().NotBeNull();
    }

    private static List<string> TextesDuDomaineMemoire(ScanViewModel vm)
        => StaApplication.Run(() =>
        {
            vm.DomaineSelectionne = vm.Domaines.First(d => d.Cle == "memoire");
            var vue = new DrDxt.App.Views.ScanView { DataContext = vm };
            vue.Measure(new System.Windows.Size(1240, 3000));
            vue.Arrange(new System.Windows.Rect(0, 0, 1240, 3000));
            vue.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
            var vus = new List<string>();
            void Parcourir(System.Windows.DependencyObject d)
            {
                if (d is System.Windows.UIElement u && u.Visibility != System.Windows.Visibility.Visible) return;
                if (d is System.Windows.Controls.TextBlock t && !string.IsNullOrEmpty(t.Text)) vus.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            return vus;
        });

    // 26/09/2026 : un changement qui n'a pas pris garde son marqueur, donc chaque scan suivant refait la
    // verification du retour. L'A/B memoire, lui, a deja eu lieu au premier retour et a remplace la
    // reference par la mesure d'apres : au scan suivant il ne doit ni remesurer, ni pretendre que la
    // mesure d'avant « ne date pas du depart ».
    [Fact]
    public async Task Apres_un_premier_retour_les_scans_suivants_ne_parlent_plus_de_l_A_B()
    {
        var fichier = new FichierFake();
        // L'ordre reel : la mesure d'avant, PUIS le depart, PUIS le retour.
        await Build(fichier: fichier).vm.ScanCommand.ExecuteAsync(null);
        await Task.Delay(300);
        var depart = DateTimeOffset.UtcNow;
        await Task.Delay(300);
        var pasPris = new BiosLoopResult(
            HadPendingChange: true, AllTook: false,
            Outcomes: new[]
            {
                new BiosSettingOutcome(
                    FirmwareSettingKind.XmpExpo, TriState.On, TriState.Off, BiosSettingVerdict.DidNotTake),
            },
            ShowRecoveryCard: true, RecoveryCardKeys: Array.Empty<string>(), MarkerCleared: false,
            DepartUtc: depart);

        var (premier, _, _) = Build(voyage: new VoyageFake(pasPris), fichier: fichier);
        await premier.ScanCommand.ExecuteAsync(null);
        premier.MemoryAb!.Lines.Should().NotBeEmpty("temoin : le premier retour compare");

        var (second, banc, _) = Build(voyage: new VoyageFake(pasPris), fichier: fichier);
        await second.ScanCommand.ExecuteAsync(null);

        banc.Lancements.Should().Be(0, "l'A/B de ce voyage a deja eu lieu");
        (second.MemoryAb?.Conclusion ?? string.Empty).Should().NotStartWith(MemoryAbStringKeys.NoVerdictStale);
    }

    // 26/09/2026 : renoncer a un changement qui n'a pas pris. Sans ceci, la section « n'a pas pris » et sa
    // carte de recuperation revenaient a chaque scan, pour toujours.
    private static BiosLoopResult PasPris() => new(
        HadPendingChange: true, AllTook: false,
        Outcomes: new[]
        {
            new BiosSettingOutcome(FirmwareSettingKind.XmpExpo, TriState.On, TriState.Off, BiosSettingVerdict.DidNotTake),
        },
        ShowRecoveryCard: true, RecoveryCardKeys: new[] { "Bios.Recovery.1" }, MarkerCleared: false);

    [Fact]
    public async Task Renoncer_retire_le_marqueur_et_la_section()
    {
        var voyage = new VoyageFake(PasPris());
        var (vm, _, _) = Build(voyage: voyage);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.HasBiosLoopResult.Should().BeTrue("temoin : la section est la");

        vm.OublierChangementBiosCommand.Execute(null);

        voyage.Oublis.Should().Be(1);
        vm.HasBiosLoopResult.Should().BeFalse();
        vm.HasMemoryAb.Should().BeFalse();
    }

    [Fact]
    public async Task Un_oubli_rate_garde_la_section_et_le_dit()
    {
        var voyage = new VoyageFake(PasPris()) { ResultatOubli = Result.Fail(ErrorCodes.IoFailure, "fichier verrouillé") };
        var (vm, _, _) = Build(voyage: voyage);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.OublierChangementBiosCommand.Execute(null);

        vm.HasBiosLoopResult.Should().BeTrue();
        vm.ErreurOubliBios.Should().Contain("Bios.Retour.OublierEchec").And.Contain("fichier verrouillé");
    }

    [Fact]
    public async Task Le_lien_pour_renoncer_s_affiche_sous_un_changement_qui_n_a_pas_pris()
    {
        var (vm, _, _) = Build(voyage: new VoyageFake(PasPris()));
        await vm.ScanCommand.ExecuteAsync(null);

        var textes = StaApplication.Run(() =>
        {
            var vue = new DrDxt.App.Views.ScanView { DataContext = vm };
            vue.Measure(new System.Windows.Size(1240, 3000));
            vue.Arrange(new System.Windows.Rect(0, 0, 1240, 3000));
            vue.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
            var vus = new List<string>();
            void Parcourir(System.Windows.DependencyObject d)
            {
                if (d is System.Windows.UIElement u && u.Visibility != System.Windows.Visibility.Visible) return;
                if (d is System.Windows.Controls.TextBlock t && !string.IsNullOrEmpty(t.Text)) vus.Add(t.Text);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    Parcourir(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(vue);
            return vus;
        });

        textes.Should().Contain("Bios.Recovery.1", "temoin : la carte de recuperation est affichee");
        textes.Should().Contain("Bios.Retour.Oublier");
    }
}
