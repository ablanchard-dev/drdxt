using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

/// <summary>
/// La frequence effective est un produit, jamais une lecture unique.
///
/// Deux sources evidentes mentent sur cette plateforme (i7-14700F, 08/09/2026) :
/// `Win32_Processor` rend `MaxClockSpeed = CurrentClockSpeed = 2100`, et
/// `CallNtPowerInformation(ProcessorInformation)` reussit en rendant `2100/2100/2100` pour les
/// 28 processeurs logiques. Les deux donnent la frequence de BASE. Un outil qui s'y fie conclut
/// « CPU a 2100/2100, aucun bridage » sur une puce qui monte a 5,4 GHz.
///
/// Et ma premiere correction a produit pire : la ligne « 2100 MHz (198 % du nominal) », deux
/// chiffres qui se contredisent dans la meme phrase. Le compteur `Processor Frequency` porte la
/// base, `% Processor Performance` porte le boost — l'effective est leur produit.
///
/// Ces tests protegent exactement cette regression : une moitie de formule ne fait pas un
/// resultat.
/// </summary>
// La résolution des noms passe par `CpuFrequencyReader.SourceDesTables`, statique, qu'une autre classe remplace
// par une fausse table ; en parallèle, ce test lisait la fausse et rougissait sous la suite complète, vert seul.
[Collection(LaTableDesCompteursNeSeRelitPasAChaqueNomTests.NomDeLaCollection)]
public class CpuFrequencyStateTests
{
    [Fact]
    public void LA_FREQUENCE_EFFECTIVE_EST_LE_PRODUIT_DE_LA_BASE_ET_DU_POURCENTAGE()
    {
        new CpuFrequencyState(2100, 198, 100, 0)
            .FrequenceEffectiveMhz.Should().BeApproximately(4158, 0.5);
    }

    [Fact]
    public void SANS_POURCENTAGE_ON_NE_REND_PAS_LA_BASE_EN_LA_FAISANT_PASSER_POUR_L_EFFECTIVE()
    {
        // Le piege exact. Rendre 2100 ici serait « vrai » au sens ou c'est une frequence lue,
        // et faux au sens ou personne ne l'a demandee — c'est ainsi qu'un outil affirme
        // « aucun boost » sur une machine qui boost.
        new CpuFrequencyState(2100, null, 100, 0).FrequenceEffectiveMhz.Should().BeNull();
    }

    [Fact]
    public void SANS_BASE_NON_PLUS()
    {
        new CpuFrequencyState(null, 198, 100, 0).FrequenceEffectiveMhz.Should().BeNull();
    }

    [Fact]
    public void ZERO_INDICATEUR_VEUT_DIRE_RIEN_NE_BRIDE_ET_NON_ON_NE_SAIT_PAS()
    {
        // Le meme piege que le masque de bridage GPU : 0 est une vraie valeur.
        new CpuFrequencyState(2100, 198, 100, 0).EstBride.Should().BeFalse();
        new CpuFrequencyState(2100, 198, 100, null).EstBride.Should().BeNull();
        new CpuFrequencyState(2100, 198, 60, 4).EstBride.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Machine")] // lit la table Perflib localisee de la vraie machine
    public void LES_NOMS_DE_COMPTEURS_SE_RESOLVENT_PAR_INDEX_SUR_CETTE_MACHINE()
    {
        // Ce test EXECUTE la resolution. Les noms sont LOCALISES : la requete anglaise echoue
        // sur ce Windows francais. Coder « Informations sur le processeur » marcherait ici et
        // nulle part ailleurs — la table `Perflib` est la seule voie portable.
        //
        // Il ne verifie pas quel nom sort (il depend de la langue), seulement que la table repond.
        var jeu = CpuFrequencyReader.Localiser("Processor Information");

        jeu.Should().NotBeNullOrWhiteSpace(
            "la table Perflib doit rendre le nom localise du jeu de compteurs processeur");
    }
}
