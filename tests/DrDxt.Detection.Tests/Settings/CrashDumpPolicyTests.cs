using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Deux constats se contredisaient dans le meme rapport.
///
/// Une section declarait `7 (automatique)` comme « le defaut Windows », classait `3` en suspect et
/// RECOMMANDAIT d'y toucher. Une autre, ecrite le meme jour, refusait de l'affirmer faute de
/// source. La page Microsoft decrit les types de vidage et **ne publie ni valeur numerique ni
/// defaut** — la seconde avait raison, et la premiere recommandait un geste sur une norme
/// inventee.
///
/// Cette machine n'exerce qu'une branche (`3`). Sans ces tests, les quatre autres seraient du code
/// que rien n'aurait jamais execute.
/// </summary>
public class CrashDumpPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(42)]
    public void AUCUN_MODE_QUI_ECRIT_N_EST_COMPARE_A_UN_DEFAUT(int mode)
    {
        // La garde centrale. `ExpectedDefault` et `NonDefault` affirment tous deux connaitre la
        // valeur d'origine, que Microsoft ne publie pas. Savoir ce qu'un mode CAPTURE ne dit rien
        // de qui l'a choisi.
        var f = CrashDumpPolicy.Juger(mode);

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().Be(RepairAction.Aucune);
        f.ValeurAttendue.Should().BeNull("il n'y a pas de valeur attendue publiee");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void AUCUNE_EXPLICATION_NE_RECOMMANDE_DE_CHANGER_LE_MODE(int mode)
    {
        // C'est la phrase exacte que la version supprimee produisait : « different du defaut
        // Windows ». Elle est desormais interdite, et le texte dit au contraire pourquoi on ne
        // compare pas.
        var f = CrashDumpPolicy.Juger(mode);

        f.Explication.Should().NotContainAny("défaut Windows", "différent du défaut");
        f.Explication.Should().Contain("sans publier de valeur par défaut");
    }

    [Fact]
    public void ZERO_EST_LE_SEUL_CAS_QUI_MERITE_UNE_RECOMMANDATION()
    {
        // Et il ne compare rien : « aucun vidage n'est ecrit » est un fait de capacite, pas un
        // ecart. C'est ce qui le rend affirmable sans defaut, comme « ce service n'a pas pu
        // demarrer » ou « bcdedit ne liste que ce qui est pose ».
        var f = CrashDumpPolicy.Juger(0);

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
        f.Explication.Should().Contain("aucune trace exploitable");
    }

    [Fact]
    public void NON_LU_N_EST_PAS_UN_MODE()
    {
        // « je n'ai pas pu lire » ne doit jamais devenir « aucun vidage », qui declencherait une
        // recommandation sur une absence de mesure.
        var f = CrashDumpPolicy.Juger(null);

        f.Statut.Should().Be(RepairStatus.Unknown);
        f.Action.Should().Be(RepairAction.Aucune);
        f.ValeurCourante.Should().BeNull();
    }

    [Fact]
    public void CHAQUE_MODE_DIT_CE_QU_IL_CAPTURE_ET_NON_CE_QU_IL_VAUT()
    {
        // Le service rendu au joueur : savoir qu'un petit vidage ne permettra pas d'accuser un
        // pilote, ce qui est la seule chose utile quand on cherche la cause d'un plantage.
        CrashDumpPolicy.Juger(3).Explication.Should().Contain("pas d'attribuer la cause à un pilote");
        CrashDumpPolicy.Juger(2).Explication.Should().Contain("piles des pilotes");
    }
}
