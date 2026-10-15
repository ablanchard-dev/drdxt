using DrDxt.Application.Autopilot;
using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Une campagne a plusieurs candidats, par la vraie session.
///
/// Deux garanties, chacune exercee par <see cref="GameOptimizationSession"/> — moteur, protocole
/// apparie, <see cref="KeepPolicy"/> — sur un banc dont l'effet de chaque levier est connu :
/// • chaque candidat est mesure seul puis garde ou annule avant que le suivant ne soit pose :
///   jamais deux leviers non juges dans la machine en meme temps ;
/// • le total de campagne ne compose que les candidats GARDES : un rejete a ete annule, son ecart
///   est reel et n'est plus dans la machine.
/// </summary>
public sealed class CampagneAPlusieursCandidatsTests
{
    private static readonly GameProfileIdentity Identite = new("Warzone.exe", null, null, null, null);

    /// <summary>Un levier dont l'effet sur toutes les grandeurs est un facteur connu.</summary>
    private sealed class Levier(string id, double facteur, List<string> evenements) : IAutopilotCandidate
    {
        public bool Applique;
        public double Facteur => facteur;

        public CandidateDescriptor Descriptor { get; } = new(
            id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, new[] { "fps.moyen", "fps.p1low" });

        public Result Apply() { evenements.Add(id + "+"); Applique = true; return Result.Ok(); }
        public Result Revert() { evenements.Add(id + "-"); Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }

    /// <summary>Le banc lit l'etat reel des leviers : ce qui est pose compte, rien d'autre.</summary>
    private sealed class Banc(IReadOnlyList<Levier> leviers) : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            var f = leviers.Where(l => l.Applique).Aggregate(1.0, (acc, l) => acc * l.Facteur);
            return Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                100 * f, 60 * f, 48 * f, new List<double> { 10.0 / f }, null, 1.0, runs,
                P95FrametimeMs: 12.0 / f, HygieneKeptFraction: 1.0)));
        }
    }

    private static (GameOptimizationProfile Profil, List<string> Evenements) Campagne()
    {
        var evenements = new List<string>();
        // L'ordre compte : le premier COUTE 10 %, le second en rend 10 %.
        var leviers = new List<Levier>
        {
            new("levier.qui_coute", 0.90, evenements),
            new("levier.qui_rend", 1.10, evenements),
        };
        var profil = new GameOptimizationSession(new Banc(leviers), () => Identite)
            .Run(1234, leviers.Cast<IAutopilotCandidate>().ToList());
        return (profil, evenements);
    }

    [Fact]
    public void Chaque_candidat_est_juge_et_rendu_AVANT_que_le_suivant_ne_soit_pose()
    {
        var (profil, evenements) = Campagne();

        // La premisse, verifiee et pas supposee.
        profil.Results.Select(r => (r.Id, r.Outcome)).Should().Equal(
            ("levier.qui_coute", CandidateOutcome.RejectedRegression),
            ("levier.qui_rend", CandidateOutcome.Kept));

        var premierDuSecond = evenements.IndexOf("levier.qui_rend+");
        var dernierDuPremier = evenements.FindLastIndex(e => e.StartsWith("levier.qui_coute", StringComparison.Ordinal));
        premierDuSecond.Should().BeGreaterThan(dernierDuPremier,
            "le second n'est pose qu'une fois le premier entierement juge");
        evenements[dernierDuPremier].Should().Be("levier.qui_coute-",
            "un candidat rejete est ANNULE avant que le suivant ne commence");
        profil.EndedInBestState.Should().BeTrue();
    }

    [Fact]
    public void Le_total_de_campagne_ne_compose_que_les_candidats_GARDES()
    {
        var (profil, _) = Campagne();

        var total = GainCumule.DepuisLaCampagne(profil.Results, GameMetricKind.Moyenne);

        total.Retenus.Should().Equal("levier.qui_rend");
        // Compter le rejete donnerait 1,10 x 0,90 - 1 = -1 % : un total que le PC ne rend pas.
        total.TotalPercent.Should().BeApproximately(10.0, 0.01);
        GainCumule.Phrase(total).Should().StartWith("PROUVÉ").And.Contain("1 levier(s) : levier.qui_rend");
    }
}
