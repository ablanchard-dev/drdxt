using System;
using System.Collections.Generic;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// D2 / lot L1 / gate G3 — LE SCORE /100 disparait, le registre le remplace.
//
// Pourquoi le score partait, et pourquoi ce n'est pas un gout d'interface : ses six leviers
// portent des magnitudes qui sont, c'est ecrit dans le code, des « estimations d'expert
// calibrees ». Penalite maximale 53 => PLANCHER MATHEMATIQUE a 47/100. Le chiffre ne pouvait
// litteralement pas dire « cette machine est mal reglee ». Et il contredisait la liste de mythes
// du produit, qui refuse tout levier non mesurable.
//
// Ce qui le remplace ne note rien : il compte. « 3 problemes prouves · 12 leviers lus sur 14 ».
// Les deux nombres sont des faits de lecture.
//
// Le piege que ces tests gardent : compter les CONSTATS au lieu des leviers sous-optimaux.
// La liste des constats contient aussi des lignes d'information et de guidage (« ReBAR non
// lisible sur NVIDIA »), et les compter gonflerait le registre avec ce qu'on n'a pas prouve.
// -----------------------------------------------------------------------
public sealed class ScanRegisterReplacesTheScoreTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "Intel", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "551.86", TriState.Off) },
        new[] { new MemoryModule("PN", "G.Skill", 4000, 3800) },
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.Off, TriState.Off, "381b4222-f694-41f0-9685-ff5bb260df2e",
        TriState.Off, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static ScoreLedgerEntry Entry(string id, LeverState state, int penalty = 0) =>
        new(id, "Performance", penalty, state, $"Finding.{id}", "Source.X");

    /// <summary>Un scan dont on choisit le grand livre, la couverture et les constats.</summary>
    private static ScanViewModel Build(
        IReadOnlyList<ScoreLedgerEntry>? ledger,
        ScanCoverage? coverage,
        IReadOnlyList<Finding>? findings = null,
        int score = 72)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(
              new BenchmarkResult(138.9, 120, 90, new List<double> { 7.0 }, null, 1.0, 5)));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, findings ?? Array.Empty<Finding>(), score, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(true, false),
              ledger, coverage)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key);
    }

    /// <summary>
    /// L'INVARIANCE — le controle qui dit si une estimation atteint encore l'ecran.
    ///
    /// Le registre compte des faits de lecture : combien de leviers ont ete lus, combien sont a
    /// corriger. Les penalites, elles, sont des estimations d'expert calibrees a la main ; c'est
    /// pour ca que le score /100 a ete retire de la vitrine le 02/09 (plancher mathematique a 47,
    /// donc incapable de dire « cette machine est mal reglee »).
    ///
    /// Si doubler toutes les penalites change ne serait-ce qu'une chaine affichee, alors une
    /// estimation decide encore de ce que le client lit — et le retrait du score n'etait qu'un
    /// deplacement.
    ///
    /// Ce test est ROUGE tant que `ScoreNarrative` vit : `VerdictText` et `BandWord` derivent du
    /// score, donc de la somme des penalites.
    /// </summary>
    [Fact]
    public async Task Doubling_every_penalty_changes_NOTHING_the_client_reads()
    {
        var coverage = new ScanCoverage(3, 3, 0);

        var normal = Build(
            new[] { Entry("xmp", LeverState.Suboptimal, 15), Entry("rebar", LeverState.Optimal),
                    Entry("vrr", LeverState.Optimal) },
            coverage, score: 85);
        await normal.ScanCommand.ExecuteAsync(null);

        // Memes leviers, memes etats, memes constats : Seules les penalites doublent, et le score
        // les suit — c'est exactement ce qu'un recalibrage d'estimation ferait.
        var doubled = Build(
            new[] { Entry("xmp", LeverState.Suboptimal, 30), Entry("rebar", LeverState.Optimal),
                    Entry("vrr", LeverState.Optimal) },
            coverage, score: 70);
        await doubled.ScanCommand.ExecuteAsync(null);

        using var _ = new FluentAssertions.Execution.AssertionScope();
        doubled.RegisterText.Should().Be(normal.RegisterText, "le registre compte, il ne note pas");
        doubled.ProvenProblemCount.Should().Be(normal.ProvenProblemCount);
        doubled.VerdictText.Should().Be(normal.VerdictText,
            "le verdict decrit ce qui a ete LU, pas une somme de points estimes");

        // PERIMETRE ELARGI le 05/09, apres coup. La premiere version de ce test ne regardait
        // que les trois lignes ci-dessus, et elle est passee au vert alors que le resume de
        // diagnostic disait encore « globalement en bonne sante » ou « demande attention » selon
        // le SCORE. Une garde juste sur un echantillon trop etroit ne garde rien : c'est le motif
        // trouve cinq fois dans la boucle de verification, et je venais de le reproduire.
        doubled.AssistantSummary.Should().Be(normal.AssistantSummary,
            "le resume parle, et il ne doit pas dire autre chose que l'ecran");
        // La liste « Résumé du diagnostic » que ce test gardait aussi a été retirée le 23/09/2026 :
        // elle redisait l'en-tête et avait fini par le contredire. La phrase ci-dessus reste.
    }

    [Fact]
    public async Task The_register_counts_the_levers_that_were_read_AND_found_wrong()
    {
        var vm = Build(
            ledger: new[]
            {
                Entry("xmp", LeverState.Suboptimal, 15),
                Entry("rebar", LeverState.Suboptimal, 3),
                Entry("vrr", LeverState.Optimal),
                Entry("x3d-ccd", LeverState.Unknown),
            },
            coverage: new ScanCoverage(ApplicableLevers: 4, KnownLevers: 3, UnknownLevers: 1));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProvenProblemCount.Should().Be(2, "xmp et rebar sont sous-optimaux ; vrr va bien, x3d n'a pas ete lu");
        vm.HasProvenProblems.Should().BeTrue();
        // La doublure de localisation rend la CLE, sans son gabarit : les nombres eux-memes sont
        // portes par les entrees resx reelles, gardees par
        // ResxPlaceholdersSurviveTranslationTests (une entree qui perd son {0} ferait disparaitre
        // le compte en silence, et ce test-ci ne le verrait pas).
        vm.RegisterText.Should().Contain("UI.Register.Problems")
            .And.Contain("UI.Register.LeversRead");
    }

    [Fact]
    public async Task An_informational_finding_is_NOT_counted_as_a_proven_problem()
    {
        // Le piege du lot. « ReBAR non lisible sur NVIDIA » est un guidage : le produit n'a rien
        // prouve, il dit qu'il n'a pas pu lire. Le compter gonflerait le registre avec exactement
        // ce que D2 lui interdit d'affirmer.
        var vm = Build(
            ledger: new[] { Entry("rebar", LeverState.Unknown) },
            coverage: new ScanCoverage(1, 0, 1),
            findings: new[]
            {
                new Finding("Gpu", Severity.Info, "Finding.Gpu.ReBarGuidance", null),
                new Finding("Gpu", Severity.Info, "Finding.Gpu.ReBarUnknown", null),
            });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Findings.Should().HaveCount(2, "les constats sont bien affiches");
        vm.ProvenProblemCount.Should().Be(0, "mais aucun n'est un probleme PROUVE");
        vm.HasProvenProblems.Should().BeFalse();
    }

    [Fact]
    public async Task A_clean_machine_says_zero_problems_and_still_says_what_it_read()
    {
        // La moitie de la promesse D1 : « si ta machine est deja bonne, on te le prouve aussi ».
        // Un registre a zero probleme doit quand meme montrer la couverture, sinon « rien trouve »
        // ne se distingue pas de « rien regarde ».
        var vm = Build(
            ledger: new[] { Entry("xmp", LeverState.Optimal), Entry("rebar", LeverState.Optimal) },
            coverage: new ScanCoverage(2, 2, 0));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProvenProblemCount.Should().Be(0);
        // 03/10/2026 (D8.4, revue n° 3) : à zéro, « aucun à changer » ; « 0 à changer » se lisait comme un démenti.
        vm.RegisterText.Should().Contain("UI.Register.None").And.NotContain("UI.Register.Problem");
        vm.RegisterText.Should().Contain("UI.Register.LeversRead");
    }

    [Fact]
    public async Task One_problem_uses_the_singular_wording()
    {
        var vm = Build(
            ledger: new[] { Entry("xmp", LeverState.Suboptimal, 15) },
            coverage: new ScanCoverage(2, 2, 0));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.RegisterText.Should().Contain("UI.Register.Problem")
            .And.NotContain("UI.Register.Problems");
    }

    [Fact]
    public async Task Without_coverage_the_register_says_only_what_it_can()
    {
        // Premiere version (02/09) : on coupait la seconde moitie et on affichait le compte seul.
        // Corrige le 05/09 : « 3 problemes prouves » sans savoir combien de leviers ont ete lus
        // laisse croire que la machine a ete examinee. Un scan qui n'a pas pu mesurer sa propre
        // couverture n'a rien examine d'affirmable, et il doit le dire.
        var vm = Build(
            ledger: new[] { Entry("xmp", LeverState.Suboptimal, 15) },
            coverage: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.RegisterText.Should().Be("UI.Register.NoCoverage");
        vm.RegisterText.Should().NotContain("UI.Register.LeversRead");
    }

    [Fact]
    public async Task A_missing_ledger_counts_zero_instead_of_guessing_from_the_score()
    {
        // Un scan sans grand livre n'a rien prouve. Retomber sur le score pour en deduire un
        // nombre de problemes ressusciterait exactement le chiffre que D2 retire.
        var vm = Build(ledger: null, coverage: new ScanCoverage(4, 4, 0), score: 47);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProvenProblemCount.Should().Be(0);
    }

    [Fact]
    public async Task A_new_scan_resets_the_register_before_it_runs()
    {
        // Sans remise a zero, un scan qui echoue laisserait a l'ecran le compte du precedent.
        var vm = Build(
            ledger: new[] { Entry("xmp", LeverState.Suboptimal, 15) },
            coverage: new ScanCoverage(2, 2, 0));

        await vm.ScanCommand.ExecuteAsync(null);
        vm.ProvenProblemCount.Should().Be(1);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.ProvenProblemCount.Should().Be(1, "et pas 2 : on compte, on n'accumule pas");
    }
}
