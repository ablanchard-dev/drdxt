using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Mesure DU 07/09/2026 : six scans d'affilee sur la machine de reference rendent **11, 14, 14,
/// 16, 16** coeurs parques -- et un scan lance pendant une compilation en rend **0**, ce qui fait
/// DISPARAITRE le seul constat Minor du produit. Le meme PC recevait donc deux verdicts selon
/// l'heure du scan, sans que rien ne le dise au client.
///
/// La cause n'est pas le levier, qui a raison d'exiger un symptome reel : c'est que le symptome
/// etait lu a un instant. Un coeur parque une seconde sur deux est parque ; une photo prise
/// pendant qu'il travaille dit le contraire.
///
/// La regle qui survit a tout ca : **parque dans un echantillon suffit**, mais **aucune lecture
/// reussie ne rend toujours `null`**. « Je n'ai pas pu lire » ne devient jamais « rien n'est
/// parque » -- c'est la distinction qui a fait proposer une ecriture sur des machines saines.
/// </summary>
public class CoreParkingSamplingTests
{
    [Fact]
    public void Parque_dans_UN_seul_echantillon_compte_comme_parque()
    {
        // Le coeur 3 ne dort que sur la deuxieme photo. Il dort quand meme.
        CoreParkingSampling.Merge(new IReadOnlyList<int>?[]
        {
            Array.Empty<int>(), new[] { 3 }, Array.Empty<int>(),
        }).Should().Equal(3);
    }

    [Fact]
    public void Les_echantillons_se_reunissent_sans_doublon_et_tries()
    {
        CoreParkingSampling.Merge(new IReadOnlyList<int>?[]
        {
            new[] { 5, 2 }, new[] { 2, 9 }, new[] { 5 },
        }).Should().Equal(2, 5, 9);
    }

    [Fact]
    public void Aucune_lecture_qui_aboutit_reste_JAMAIS_LU()
    {
        // Et surtout pas une liste vide : c'est la confusion qui a fait proposer une ecriture sur
        // des machines parfaitement saines.
        CoreParkingSampling.Merge(new IReadOnlyList<int>?[] { null, null, null }).Should().BeNull();
    }

    [Fact]
    public void Une_seule_lecture_qui_aboutit_suffit_a_rendre_un_resultat()
    {
        CoreParkingSampling.Merge(new IReadOnlyList<int>?[] { null, new[] { 7 }, null })
            .Should().Equal(7);
    }

    [Fact]
    public void Lu_partout_et_rien_de_parque_rend_une_liste_VIDE_pas_null()
    {
        // « Lu, et aucun coeur n'est parque » est un resultat, et le levier a le droit de s'en
        // servir pour dire que la machine va bien.
        CoreParkingSampling.Merge(new IReadOnlyList<int>?[]
        {
            Array.Empty<int>(), Array.Empty<int>(),
        }).Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Le_produit_ne_juge_JAMAIS_sur_une_seule_photo()
    {
        // Ce test ne verifie pas un calcul, il verrouille une INTENTION : ramener le nombre
        // d'echantillons a 1 ferait revenir exactement le defaut mesure le 07/09 -- un verdict qui
        // clignote selon l'occupation de la machine -- sans qu'aucun test de calcul ne bronche,
        // puisque la fusion, elle, resterait juste.
        CoreParkingSampling.Echantillons.Should().BeGreaterThan(1);
        CoreParkingSampling.PauseMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sans_aucun_echantillon_on_ne_conclut_rien()
    {
        CoreParkingSampling.Merge(Array.Empty<IReadOnlyList<int>?>()).Should().BeNull();
    }
}
