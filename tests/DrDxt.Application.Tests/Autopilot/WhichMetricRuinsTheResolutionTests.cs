using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Une resolution qui dit « +/-74 % » ne dit pas **qui** LA RUINE.
///
/// Session Warzone reelle du 07/09/2026 : la partie resolvait +/-74,0 % sur le score
/// Competitive, et un candidat qui n'ecrit rien y a rendu -0,2 %. Verdict correct — mais la
/// session s'arretait la, sans dire si la moyenne, elle, restait decidable. Or les deux grandeurs
/// du score n'ont aucune raison d'etre aussi dispersees : a 15 s et 140 fps, la moyenne porte
/// ~2 100 frames quand le 1 % low n'en porte que ~21. Un percentile sur 21 tirages disperse
/// enormement, et il entre dans le score a 65 %.
///
/// Ce test grave la separation : les MEMES passes d'A/A, reprobees grandeur par grandeur, doivent
/// designer la coupable. C'est ce qui permet a une session de dire « la moyenne est decidable, le
/// 1 % low ne l'est pas » au lieu de « rien n'est decidable ».
///
/// Ces deux resolutions n'AUTORISENT rien : le score composite reste seul juge d'un keep. Elles
/// expliquent une resolution, elles ne la contournent pas.
/// </summary>
public sealed class WhichMetricRuinsTheResolutionTests
{
    private static readonly GameProfileIdentity Identite = new(
        "cod.exe", "NVIDIA RTX 4060", "616.56", "Windows 11 26200", "1728x1080 @ 200 Hz");

    private sealed class BancScripte : IBenchmarkRunner
    {
        private readonly Queue<GameMetrics> _passes;
        public BancScripte(params GameMetrics[] passes) => _passes = new(passes);

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            var m = _passes.Count > 0 ? _passes.Dequeue() : new GameMetrics(140, 85);
            return Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                m.AvgFps, m.P1LowFps, m.P1LowFps * 0.9,
                new List<double> { 1000.0 / m.AvgFps }, null, 1.0, runs, HygieneKeptFraction: 1.0)));
        }
    }

    private sealed class CandidatInerte : IAutopilotCandidate
    {
        public CandidateDescriptor Descriptor { get; } = new(
            "controle", CandidateKind.AutoHot, RiskLevel: 0, NeedsStability: false,
            new[] { "fps.moyen" });

        public Result Apply() => Result.Ok();
        public Result Revert() => Result.Ok();
        public Result<bool> ReadBack() => Result<bool>.Ok(true);
    }

    /// <summary>La moyenne est stable, le 1 % low part dans tous les sens. Le produit doit savoir
    /// le dire, et non rendre un seul chiffre qui melange les deux.</summary>
    [Fact]
    public void Quand_le_1_pourcent_low_disperse_seul_la_session_le_NOMME()
    {
        var banc = new BancScripte(
            new GameMetrics(140, 60),
            new GameMetrics(141, 110),
            new GameMetrics(140, 85));

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatInerte() });

        profil.ResolutionMoyenne!.Verdict.Should().Be(SceneVerdict.Usable);
        profil.ResolutionMoyenne!.ResolutionPct.Should().BeLessThan(10,
            "la moyenne porte des milliers de frames : elle ne bouge pas de 10 % d'une passe a l'autre");

        profil.ResolutionUnPourcentLow!.ResolutionPct.Should().BeGreaterThan(50,
            "un percentile sur ~21 frames disperse enormement");

        // Corrige le 07/09/2026. Ce test verifiait que la resolution de la SCENE etait pire que
        // celle de la moyenne, parce que la scene etait alors probee sur le score composite. Elle
        // l'est desormais sur la moyenne : la derive est une propriete de la scene, et la moyenne
        // est la grandeur qui la lit le mieux. Ce qu'il reste a prouver — et qui est le vrai sujet
        // — c'est l'ECART entre les deux grandeurs.
        profil.ResolutionUnPourcentLow!.ResolutionPct
            .Should().BeGreaterThan(profil.ResolutionMoyenne!.ResolutionPct * 5,
                "c'est bien le 1 % low, et lui seul, qui ruine la mesure");
    }

    /// <summary>Le controle negatif : quand les deux grandeurs sont sages, la separation ne
    /// fabrique aucun coupable.</summary>
    [Fact]
    public void Quand_les_DEUX_sont_stables_aucune_des_deux_n_est_accusee()
    {
        var banc = new BancScripte(
            new GameMetrics(140, 100),
            new GameMetrics(141, 101),
            new GameMetrics(140, 100));

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatInerte() });

        profil.ResolutionMoyenne!.ResolutionPct.Should().BeLessThan(10);
        profil.ResolutionUnPourcentLow!.ResolutionPct.Should().BeLessThan(10);
    }

    /// <summary>Sous trois passes il n'y a rien a separer — et le produit doit rendre `null`, pas
    /// un chiffre de complaisance.</summary>
    [Fact]
    public void Sous_trois_passes_exploitables_la_separation_ne_pretend_rien()
    {
        var banc = new BancScripte(new GameMetrics(140, 100));

        var profil = new GameOptimizationSession(banc, () => Identite)
            .Run(1234, new IAutopilotCandidate[] { new CandidatInerte() }, passesAa: 2);

        profil.ResolutionMoyenne.Should().BeNull();
        profil.ResolutionUnPourcentLow.Should().BeNull();
    }
}
