using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'arret sur significativite est de l'optional stopping, et j'ai ecrit le contraire.
///
/// Le moteur s'arretait des qu'un gain devenait significatif, avec cette justification, ecrite
/// noir sur blanc dans le code et dans un rapport : « un Unresolved ne stoppe jamais, donc les
/// regards repetes ne gonflent pas les faux positifs ».
///
/// C'est **faux**, et c'est faux exactement a l'envers de l'intuition. Regarder n fois offre n
/// occasions de franchir le seuil par hasard. Ne s'arreter que sur les succes ne corrige rien —
/// c'est precisement ce qui cree le biais, puisque les franchissements fortuits sont convertis en
/// verdicts definitifs pendant que les non-franchissements obtiennent une chance de plus. Un
/// intervalle nominal a 95 % regarde six fois ne tient plus 95 %.
///
/// Un arret sequentiel correct existe (alpha-spending, confidence sequences) : il DEPENSE le
/// risque au fil des regards. Tant qu'il n'est pas implemente et calibre, le protocole va au bout.
///
/// Les arrets PHYSIQUES restent permis — crash, TDR, temperature, focus perdu, capture
/// invalide, relecture impossible. Ils ne dependent pas du resultat mesure, donc ils ne peuvent
/// pas biaiser le verdict. C'est le seul critere qui compte.
/// </summary>
public class FixedProtocolNoOptionalStoppingTests
{
    private sealed class CandidatCompteur : IAutopilotCandidate
    {
        private readonly double _facteur;

        public CandidatCompteur(double facteur) => _facteur = facteur;

        public CandidateDescriptor Descriptor { get; } = new(
            "essai", CandidateKind.AutoHot, RiskLevel: 0, NeedsStability: false, new[] { "fps.moyen" });

        public bool Applique { get; private set; }
        public double Facteur => Applique ? _facteur : 1.0;

        public Result Apply() { Applique = true; return Result.Ok(); }
        public Result Revert() { Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }

    private static (AutopilotProfile Profil, int Passes) Exercer(double facteur, int alternances)
    {
        var c = new CandidatCompteur(facteur);
        var appels = 0;
        var profil = AutopilotEngine.Run(
            new IAutopilotCandidate[] { c },
            () => { appels++; return 100.0 * c.Facteur; },
            passesAa: 3, stable: null, but: OptimizationGoal.Competitive,
            alternancesMax: alternances);
        return (profil, appels - 3);   // moins les trois passes d'A/A
    }

    [Fact]
    public void LE_PROTOCOLE_EST_FIXE_un_gain_ENORME_ne_l_ecourte_PAS()
    {
        // Un +50 % franchit le seuil au premier bloc : s'arreter la, a 4 passes, ferait reveler
        // le resultat par le nombre de passes — exactement la definition de l'optional stopping.
        var (_, passesGain) = Exercer(facteur: 1.50, alternances: 12);

        passesGain.Should().Be(12, "le protocole va au bout, quel que soit ce qu'il voit");
    }

    [Fact]
    public void UNE_REGRESSION_ENORME_ne_l_ecourte_PAS_NON_PLUS()
    {
        var (_, passesRegression) = Exercer(facteur: 0.50, alternances: 12);

        passesRegression.Should().Be(12);
    }

    [Fact]
    public void LE_NOMBRE_DE_PASSES_NE_REVELE_RIEN_DU_RESULTAT()
    {
        // La formulation la plus forte de la propriete : un observateur qui ne verrait que la
        // duree de l'experience ne doit rien pouvoir en deduire. Gain, regression, ou rien du
        // tout — meme cout.
        var (_, gain) = Exercer(facteur: 1.50, alternances: 8);
        var (_, regression) = Exercer(facteur: 0.50, alternances: 8);
        var (_, rien) = Exercer(facteur: 1.00, alternances: 8);

        gain.Should().Be(regression);
        regression.Should().Be(rien);
    }

    [Fact]
    public void Le_verdict_lui_meme_reste_rendu_sur_l_experience_COMPLETE()
    {
        // Le controle positif : fermer l'arret anticipe ne doit pas rendre le moteur muet.
        var (profil, passes) = Exercer(facteur: 1.50, alternances: 12);

        passes.Should().Be(12);
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
    }
}
