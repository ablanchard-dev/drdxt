using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// 26/09/2026 — l'allowlist comparait un chemin rogne (espaces et antislashs de bord retires), et
/// l'executeur ecrivait le chemin d'origine. Dans le registre, « Software\X » et « Software\X » avec
/// une espace devant sont deux cles : la variante passait pour la feuille permise et l'ecriture
/// partait dans une cle fantome. Le chemin verifie doit etre celui qui est ecrit.
/// </summary>
public sealed class UnCheminDeRegistreNonCanoniqueEstRefuseTests
{
    private const string Feuille = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private static bool Permis(string chemin) => new OpAllowlist().Validate(
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, chemin, "HwSchMode", RegistryValueType.DWord, "2")).IsOk;

    [Theory]
    [InlineData(" " + Feuille)]
    [InlineData(Feuille + " ")]
    [InlineData(@"\" + Feuille)]
    [InlineData(Feuille + @"\")]
    public void Une_variante_de_la_feuille_permise_est_refusee(string variante)
        => Permis(variante).Should().BeFalse();

    [Fact]
    public void Le_temoin_la_forme_canonique_passe()
        => Permis(Feuille).Should().BeTrue();

    // 26/09/2026 : la feuille permise acceptait n'importe quel DWORD. Le levier HAGS n'est propose que
    // HAGS eteint : il ecrit 2 (allume) et son inverse 1 (eteint), rien d'autre (HagsOnTweak).
    [Theory]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("0", false)]
    [InlineData("3", false)]
    [InlineData("4294967295", false)]
    public void HwSchMode_n_accepte_que_ses_deux_valeurs(string valeur, bool permis)
        => new OpAllowlist().Validate(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Feuille, "HwSchMode",
            RegistryValueType.DWord, valeur)).IsOk.Should().Be(permis);
}
