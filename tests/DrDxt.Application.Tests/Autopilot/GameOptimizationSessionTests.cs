using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// « OPTIMISER CE JEU » — la verticale, testee de bout en bout sans machine.
///
/// Ce qui est verifie ici n'est pas que la boucle tourne (le moteur a ses propres tests) mais que
/// **la mesure du JEU decide**, et rien d'autre :
///   * la grandeur mesuree est le SCORE d'objectif, pas les FPS moyens — sinon un 1 % low qui
///     s'effondre pendant que la moyenne monte passerait pour un gain ;
///   * une partie trop bruitee produit « aucun reglage conserve » avec son explication, ce qui est
///     un resultat, pas un echec ;
///   * le profil porte l'identite de ce qui a ete optimise, sinon il sera un jour applique a une
///     autre machine.
/// </summary>
public sealed class GameOptimizationSessionTests
{
    private static readonly GameProfileIdentity Identite = new(
        "Warzone.exe", "NVIDIA RTX 4060", "616.56", "Windows 11 26200", "2560x1440 @ 200 Hz");

    /// <summary>Un banc scripte : chaque appel rend les metriques suivantes de la liste.</summary>
    private sealed class BancScripte : IBenchmarkRunner
    {
        private readonly Queue<GameMetrics> _passes;
        public int Appels;

        public BancScripte(params GameMetrics[] passes) => _passes = new(passes);

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            Appels++;
            var m = _passes.Count > 0 ? _passes.Dequeue() : new GameMetrics(0, 0);
            return Task.FromResult(m.AvgFps > 0
                ? Result<BenchmarkResult>.Ok(new BenchmarkResult(
                    m.AvgFps, m.P1LowFps, m.P01LowFps ?? m.P1LowFps * 0.8,
                    new List<double> { 1000.0 / m.AvgFps }, null, 1.0, runs, HygieneKeptFraction: 1.0))
                : Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        }
    }

    /// <summary>Un candidat qui change les metriques du jeu quand il est applique.</summary>
    private sealed class CandidatFake : IAutopilotCandidate
    {
        public bool Applique;
        public CandidateDescriptor Descriptor { get; } = new(
            "gpu.core_offset", CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false,
            new[] { "fps.moyen", "fps.p1low" });

        public Result Apply() { Applique = true; return Result.Ok(); }
        public Result Revert() { Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }

    private static GameMetrics[] Repeter(GameMetrics m, int n) => Enumerable.Repeat(m, n).ToArray();

    [Fact]
    public void Une_partie_TROP_BRUITEE_ne_teste_rien_et_le_DIT()
    {
        // « Aucun reglage conserve : cette partie etait trop bruitee » est un RESULTAT. C'est
        // infiniment preferable a un faux +3 %, et le joueur doit comprendre pourquoi.
        var banc = new BancScripte(
            new GameMetrics(135, 80), new GameMetrics(140, 88), new GameMetrics(148, 96));
        var candidat = new CandidatFake();

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { candidat });

        profil.Scene.Verdict.Should().Be(SceneVerdict.DriftDominant);
        profil.Kept.Should().BeEmpty();
        profil.NotTestable.Should().ContainSingle();
        profil.SceneExplanation.Should().Contain("dérive").And.Contain("temps qui passe");
        candidat.Applique.Should().BeFalse("rien ne doit avoir ete ecrit sur la machine");
    }

    [Fact]
    public void Sans_jeu_du_tout_la_session_n_invente_aucune_baseline()
    {
        var profil = new GameOptimizationSession(new BancScripte(), () => Identite)
            .Run(targetPid: 0, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Baseline.Should().BeNull("aucune mesure n'a abouti");
        profil.Kept.Should().BeEmpty();
    }

    [Fact]
    public void La_BASELINE_affichee_est_la_premiere_passe_exploitable()
    {
        var banc = new BancScripte(Repeter(new GameMetrics(137.2, 88.1), 12));

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Baseline!.AvgFps.Should().Be(137.2);
        profil.Baseline.P1LowFps.Should().Be(88.1);
    }

    [Fact]
    public void Le_profil_porte_l_IDENTITE_de_ce_qui_a_ete_optimise()
    {
        // Un profil sans identite sera un jour applique a un autre pilote ou une autre resolution,
        // et promettra un gain que plus rien ne soutient.
        var profil = new GameOptimizationSession(new BancScripte(Repeter(new GameMetrics(137, 88), 12)),
                () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Identity.ProcessName.Should().Be("Warzone.exe");
        profil.Identity.GpuDriver.Should().Be("616.56");
        profil.Identity.DisplayMode.Should().Contain("200 Hz");
        profil.Goal.Should().Be(OptimizationGoal.Competitive);
    }

    [Fact]
    public void La_session_explique_en_francais_ce_qu_elle_pouvait_voir()
    {
        var profil = new GameOptimizationSession(new BancScripte(Repeter(new GameMetrics(137, 88), 12)),
                () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        // L'écran dit la règle qui décide (KeepPolicy : 1 % low ou p95 prouvé meilleur, sans régression), pas des poids
        // qu'aucun verdict n'applique, ni une résolution d'A/A que le verdict n'utilise pas.
        profil.SceneExplanation.Should().Contain("1 % low").And.Contain("95e centile")
            .And.NotContain("65").And.NotContain("indiscernable");
    }

    [Fact]
    public void UNE_SESSION_SANS_SCENE_DECLAREE_DIT_CE_QU_ELLE_N_AUTORISE_PAS()
    {
        // Un appel a `Run` sans contrat laisse la scene `Inconnue` : un reglage trouve la ne doit pas
        // etre garde comme un reglage valide en pleine partie, sans un mot.
        var profil = new GameOptimizationSession(new BancScripte(Repeter(new GameMetrics(137, 88), 12)),
                () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Scene2!.Representativite.Should().Be(SceneRepresentativity.Unknown);
        profil.SceneExplanation.Should().Contain("personne n'a dit ce qui était joué",
            "un profil ne se defend pas sur une scene dont on ignore tout");
    }

    [Fact]
    public void UNE_SCENE_DE_GAMEPLAY_VALIDEE_N_AJOUTE_AUCUNE_RESERVE()
    {
        // Le contre-cas, sans lequel la garde precedente passerait aussi avec une phrase collee
        // sans condition : quand la scene autorise le keep, la session n'invente pas de reserve.
        var profil = new GameOptimizationSession(new BancScripte(Repeter(new GameMetrics(137, 88), 12)),
                () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() },
                scene: new SceneContract(SceneRepresentativity.Gameplay, SceneRole.Validation,
                    "partie reelle, rotation complete"));

        profil.SceneExplanation.Should().Contain("95e centile")
            .And.NotContain("⚠️", "rien ne manque a cette scene : elle ne merite aucune reserve");
    }

    [Fact]
    public void UNE_DECOUVERTE_EN_PARTIE_REELLE_RECLAME_ENCORE_SA_VALIDATION()
    {
        // Le piege du coin calme : une vraie partie, mais une scene choisie pour voir clair. Le
        // keep y est defendable aujourd'hui, pas durablement — et c'est la seule difference entre
        // les deux roles.
        var profil = new GameOptimizationSession(new BancScripte(Repeter(new GameMetrics(137, 88), 12)),
                () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() },
                scene: new SceneContract(SceneRepresentativity.Gameplay, SceneRole.Decouverte,
                    "partie reelle, zone calme"));

        profil.SceneExplanation.Should().Contain("à revalider",
            "un candidat trouve en decouverte n'est pas encore un profil defendable");
    }

    [Fact]
    public void UNE_PASSE_JOUEE_AILLEURS_NE_COMPTE_PAS_ET_LA_SESSION_LE_DIT()
    {
        // Le focus est desormais observe, pas devine. Une passe passee derriere une autre
        // fenetre ne dit rien des performances en jeu — elle ne compte pas, exactement comme une
        // passe sans frame. Et la session le nomme, au lieu de rendre un resultat inexplicable.
        var banc = new BancFocus(fractionAuPremierPlan: 0.20);

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Kept.Should().BeEmpty();
        profil.SceneExplanation.Should().Contain("premier plan",
            "le joueur doit savoir POURQUOI sa session n'a rien donne");
    }

    [Fact]
    public void UN_JEU_QUI_RAME_MAIS_RESTE_AU_PREMIER_PLAN_EST_UNE_VRAIE_DONNEE()
    {
        // La regle a ne jamais perdre : le focus dit si la mesure porte sur le jeu, il ne dit
        // Rien de la performance. Une partie catastrophique entierement jouee reste mesurable —
        // on ne nettoie pas les mauvaises performances du joueur.
        var banc = new BancFocus(fractionAuPremierPlan: 1.0, avgFps: 34, p1Low: 12);

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        profil.Baseline!.AvgFps.Should().Be(34, "34 FPS est une mesure, pas un artefact");
        profil.SceneExplanation.Should().NotContain("premier plan");
    }

    /// <summary>Un banc dont on fixe le focus observe et les metriques.</summary>
    private sealed class BancFocus : IBenchmarkRunner
    {
        private readonly double _focus, _avg, _low;

        public BancFocus(double fractionAuPremierPlan, double avgFps = 137, double p1Low = 88)
        {
            _focus = fractionAuPremierPlan; _avg = avgFps; _low = p1Low;
        }

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
            => Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                _avg, _low, _low * 0.8, new List<double> { 1000.0 / _avg }, null, 1.0, runs,
                HygieneKeptFraction: 1.0, ForegroundFraction: _focus)));
    }

    [Fact]
    public void UN_1_POURCENT_LOW_QUI_S_EFFONDRE_N_EST_PAS_UN_GAIN_MEME_SI_LA_MOYENNE_MONTE()
    {
        // Le test qui justifie de mesurer le SCORE et pas les FPS moyens. Le reglage fait monter
        // la moyenne de 4 % et effondrer le 1 % low de 25 % : le jeu devient plus saccade. Un
        // autopilote qui optimise la moyenne garderait ce reglage.
        var stableEtat = new GameMetrics(100, 60);
        var avecReglage = new GameMetrics(104, 45);

        var candidat = new CandidatFake();
        var banc = new Bascule(candidat, stableEtat, avecReglage);

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { candidat });

        profil.Kept.Should().BeEmpty("la fluidite s'est degradee");
        profil.Reverted.Should().ContainSingle();
        candidat.Applique.Should().BeFalse("la machine doit finir sans le reglage");
    }

    /// <summary>Un banc qui rend un etat ou l'autre selon que le candidat est applique — la seule
    /// facon de tester la boucle a-b-a-b de bout en bout.</summary>
    private sealed class Bascule : IBenchmarkRunner
    {
        private readonly CandidatFake _c;
        private readonly GameMetrics _a, _b;

        public Bascule(CandidatFake c, GameMetrics a, GameMetrics b) { _c = c; _a = a; _b = b; }

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            var m = _c.Applique ? _b : _a;
            return Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                m.AvgFps, m.P1LowFps, m.P1LowFps * 0.8,
                new List<double> { 1000.0 / m.AvgFps }, null, 1.0, runs, HygieneKeptFraction: 1.0)));
        }
    }
}
