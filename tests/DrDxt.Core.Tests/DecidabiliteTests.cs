using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// PHASE 18 F5 — un candidat ne se tente que si la scene peut voir son effet, et le nombre
/// d'alternances est fixe avant la mesure, par l'effet declare.
/// </summary>
public class DecidabiliteTests
{
    private static SceneResolution Scene(double resolutionPct) =>
        new(resolutionPct, 0, 24, SceneVerdict.Usable);

    // 26/09/2026 : moins de trois passes, la resolution vaut +inf ; le motif ecrivait « (∞ %) ».
    [Fact]
    public void Une_scene_sans_resolution_le_dit_sans_symbole_infini()
    {
        var scene = new SceneResolution(double.PositiveInfinity, 0, 2, SceneVerdict.TooFewRuns);

        var motif = Decidabilite.NonDecidable(8, scene, SceneRepresentativity.Entrainement);

        motif.Should().NotBeNull().And.NotContain("∞").And.NotContain("Infinity").And.Contain("moins de 3 passes");
    }

    [Theory]
    [InlineData(15.0, 12)]
    [InlineData(10.0, 12)]
    [InlineData(6.0, 24)]
    public void Les_alternances_se_fixent_par_l_effet_DECLARE(double effet, int attendu)
        => Decidabilite.AlternancesPour(effet).Should().Be(attendu);

    [Fact]
    public void Un_effet_SOUS_la_resolution_n_est_pas_decidable()
        => Decidabilite.NonDecidable(2, Scene(5.4), SceneRepresentativity.Entrainement)
            .Should().Contain("résolution");

    [Fact]
    public void Un_effet_AU_DESSUS_de_la_resolution_se_tente()
        => Decidabilite.NonDecidable(8, Scene(5.4), SceneRepresentativity.Gameplay).Should().BeNull();

    [Fact]
    public void Sous_5_pour_cent_une_PARTIE_REELLE_renvoie_vers_un_entrainement()
        // Meme si cette scene-ci resolvait 2 %, on ne conclut pas sous 5 % en partie reelle.
        => Decidabilite.NonDecidable(4, Scene(2), SceneRepresentativity.Gameplay)
            .Should().Contain("entraînement");

    [Fact]
    public void Sur_un_ENTRAINEMENT_le_meme_effet_se_tente_si_la_scene_le_voit()
        => Decidabilite.NonDecidable(4, Scene(2), SceneRepresentativity.Entrainement).Should().BeNull();

    // ── De bout en bout dans le moteur ──────────────────────────────────────────────────────

    private sealed class Candidat(double? effet, double facteur = 1.0) : IAutopilotCandidate
    {
        public int Ecritures { get; private set; }
        public bool Applique { get; private set; }
        public double Facteur => Applique ? facteur : 1.0;

        public CandidateDescriptor Descriptor { get; } = new(
            "essai", CandidateKind.AutoHot, RiskLevel: 0, NeedsStability: false, new[] { "fps.moyen" },
            EffetAttenduPct: effet);

        public Result Apply() { Ecritures++; Applique = true; return Result.Ok(); }
        public Result Revert() { Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }

    /// <summary>A/A BRUITE (resolution large), puis mesures stables.</summary>
    private static (AutopilotProfile Profil, int Passes) Exercer(Candidat c)
    {
        var aa = new Queue<double>(new[] { 100.0, 108.0, 95.0 });
        var appels = 0;
        var profil = AutopilotEngine.Run(
            new IAutopilotCandidate[] { c },
            () => { appels++; return aa.Count > 0 ? aa.Dequeue() : 100.0 * c.Facteur; },
            passesAa: 3, stable: null, but: OptimizationGoal.Competitive, alternancesMax: 4);
        return (profil, appels - 3);
    }

    [Fact]
    public void Un_candidat_NON_DECIDABLE_n_est_jamais_ecrit()
    {
        var c = new Candidat(effet: 2);

        var (profil, passes) = Exercer(c);

        c.Ecritures.Should().Be(0, "on ne depense ni une ecriture ni une passe sur un effet invisible");
        passes.Should().Be(0);
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.NotAttempted);
    }

    [Fact]
    public void Un_effet_FORT_fixe_12_alternances_d_avance_quel_que_soit_le_parametre()
    {
        var c = new Candidat(effet: 200, facteur: 3.0);

        Exercer(c).Passes.Should().Be(12, "le nombre est pre-enregistre par l'effet declare");
    }

    [Fact]
    public void Sans_effet_declare_le_moteur_se_comporte_comme_avant()
    {
        var c = new Candidat(effet: null);

        var (_, passes) = Exercer(c);

        c.Ecritures.Should().BeGreaterThan(0);
        passes.Should().Be(4, "alternancesMax = 4, comme avant cette regle");
    }
}
