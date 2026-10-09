using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le produit etait aveugle parce qu'il demandait son bruit au mauvais endroit.
///
/// L'effet apparie etait juge contre la resolution d'une phase d'A/A a trois passes. Trois passes,
/// c'est t95(1) = 12,706 — six fois la valeur asymptotique, le pire estimateur de bruit qu'on
/// puisse construire. Le bruit annonce ne descendait donc jamais, quel que soit le nombre
/// d'alternances ajoutees ensuite : allonger l'experience ameliorait l'effet sans jamais ameliorer
/// le seuil auquel on le comparait.
///
/// Mesure du 07/09/2026 (`tools/tailbench`, 8 captures Warzone reelles, protocole glissant) :
///
///   fenetres 15 s   annonce par l'A/A   bruit vrai (distribution nulle)
///   4 alternances        15,7 %              13,7 %
///   8 alternances        14,3 %               5,2 %
///   12 alternances       14,3 %               3,9 %      ← l'annonce ne bouge plus, le bruit si
///
/// Depuis, l'experience appariee annonce sa propre resolution : var(c) = σ²·Stt/det, avec σ² le
/// residu du modele et df = n−3. Elle profite de toutes les passes et decrit le bruit reellement
/// present pendant l'experience. Recalibree sur les memes captures :
///
///   8 alternances    annonce 5,3 %   vrai 5,2 %
///   12 alternances   annonce 4,6 %   vrai 3,9 %
///   24 alternances de 10 s   annonce 3,3 %   vrai 2,7 %   (0 % de faux positifs)
///
/// Ces tests gravent les deux proprietes qui comptent, sans dependre d'un jeu de donnees reel :
/// la resolution annoncee doit retrecir quand on ajoute des alternances, et elle doit couvrir
/// l'effet mesure quand il n'y a pas d'effet.
/// </summary>
public class PairedModelKnowsItsOwnNoiseTests
{
    /// <summary>Une sequence a-b-b-a repetee en miroir : chaque bloc de quatre est orthogonal au
    /// temps, donc l'allonger ajoute des donnees sans ajouter de derive.</summary>
    private static bool[] Etats(int n)
    {
        var motif = new[] { false, true, true, false };
        return Enumerable.Range(0, n).Select(k => (k / 4) % 2 == 0 ? motif[k % 4] : !motif[k % 4]).ToArray();
    }

    /// <summary>Des passes bruitees sans aucun effet et sans derive : tout ce que le modele y
    /// trouvera est une erreur, et sa resolution annoncee doit la couvrir.</summary>
    private static List<AbPass> Passes(int n, double bruit, int graine, double effetPct = 0)
    {
        var etats = Etats(n);
        var alea = new Random(graine);
        return Enumerable.Range(0, n).Select(k =>
        {
            var basefps = 140.0 * (etats[k] ? 1 + effetPct / 100.0 : 1.0);
            return new AbPass(etats[k], basefps + (alea.NextDouble() * 2 - 1) * bruit);
        }).ToList();
    }

    /// <summary>La resolution mediane sur trente tirages independants. Sur un tirage la
    /// propriete est fausse : a quatre passes le residu n'a qu'un degre de liberte et peut sortir
    /// minuscule par chance — mesure ici meme, graine 42, 2,00 % a quatre passes contre 2,43 % a
    /// douze. Ce n'est pas un contre-exemple, c'est la variance de l'estimateur de variance. La
    /// propriete est statistique, elle doit etre testee comme telle.</summary>
    private static double ResolutionMediane(int passes, double bruit)
    {
        var v = Enumerable.Range(0, 30)
            .Select(g => PairedAb.Analyse(Passes(passes, bruit, g), double.PositiveInfinity)
                .Game!.Preuve.ResolutionPct)
            .Where(double.IsFinite)
            .OrderBy(x => x)
            .ToList();
        return v[v.Count / 2];
    }

    [Fact]
    public void AJOUTER_DES_ALTERNANCES_RETRECIT_LA_RESOLUTION_ANNONCEE()
    {
        // C'est exactement ce que l'ancienne version ne savait pas faire : la resolution venait
        // de trois passes d'A/A et restait bloquee quel que soit le reste du protocole. Sur les
        // captures Warzone reelles, l'annonce restait a ~14 % de 4 a 12 alternances pendant que le
        // bruit vrai tombait de 13,7 % a 3,9 %.
        var quatre = ResolutionMediane(4, bruit: 5);
        var douze = ResolutionMediane(12, bruit: 5);
        var vingtQuatre = ResolutionMediane(24, bruit: 5);

        douze.Should().BeLessThan(quatre);
        vingtQuatre.Should().BeLessThan(douze);
    }

    [Fact]
    public void SANS_EFFET_LA_RESOLUTION_ANNONCEE_COUVRE_CE_QUE_LE_MODELE_TROUVE()
    {
        // Le controle negatif, repete : sur vingt tirages sans effet, le modele ne doit presque
        // jamais conclure. Un intervalle a 95 % a le droit de se tromper, pas de se tromper
        // souvent — et surtout pas systematiquement.
        var conclusions = 0;
        for (var graine = 0; graine < 20; graine++)
        {
            var preuve = PairedAb.Analyse(Passes(12, bruit: 5, graine), double.PositiveInfinity)
                .Game!.Preuve;
            if (preuve.EstConcluante) conclusions++;
        }

        conclusions.Should().BeLessThanOrEqualTo(2,
            "un intervalle a 95 % doit se tromper environ une fois sur vingt, pas une fois sur trois");
    }

    [Fact]
    public void UN_VRAI_EFFET_AU_DESSUS_DU_BRUIT_RESTE_PROUVE()
    {
        // Le controle POSITIF. Un seuil qui ne conclut jamais serait parfaitement « honnete » et
        // parfaitement inutile : ce test interdit de rendre le produit sur en le rendant aveugle.
        var preuve = PairedAb.Analyse(Passes(16, bruit: 2, graine: 7, effetPct: 10), double.PositiveInfinity)
            .Game!.Preuve;

        preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        preuve.DeltaPct.Should().BeApproximately(10, 2);
    }

    [Fact]
    public void QUATRE_PASSES_NE_LAISSENT_QU_UN_SEUL_DEGRE_DE_LIBERTE_ET_LE_MODELE_LE_DIT()
    {
        // Trois parametres ajustes sur quatre points : df = 1, t95 = 12,706. La resolution annoncee
        // doit etre ENORME — c'est le prix du protocole minimal, et le produit doit le porter au
        // lieu de le masquer derriere un chiffre rassurant.
        ResolutionMediane(4, bruit: 5).Should().BeGreaterThan(10);
    }
}
