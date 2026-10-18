using DrDxt.App.Platform;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.App.Tests;

/// <summary>
/// L'origine d'un levier de registre est réécrite par son annulation avec le type de sa feuille (un seul par feuille dans
/// l'allowlist). Une origine d'un autre type, lue comme si c'était le bon (un REG_SZ « 1 » pris pour le DWORD 1), revenait
/// avec un type qu'elle n'avait pas. Lue « pas du bon type », elle n'est pas une origine : le levier ne touche à rien.
/// </summary>
public sealed class TypeDuRegistreConserveTests
{
    [Fact]
    public void Seul_un_vrai_REG_DWORD_est_un_DWORD()
    {
        Win32RegistryReader.Dword(1).Value.Should().Be(1u);
        Win32RegistryReader.Dword(null).Value.Should().BeNull("absente, l'annulation est une suppression");
        Win32RegistryReader.Dword("1").IsOk.Should().BeFalse("un REG_SZ « 1 » reviendrait en REG_DWORD");
        Win32RegistryReader.Dword(1L).IsOk.Should().BeFalse("un REG_QWORD reviendrait en REG_DWORD");
    }

    [Fact]
    public void Seul_un_vrai_REG_SZ_est_un_texte()
    {
        Win32RegistryReader.Texte("1", RegistryValueKind.String).Value.Should().Be("1");
        Win32RegistryReader.Texte(null, RegistryValueKind.Unknown).Value.Should().BeNull();
        Win32RegistryReader.Texte(1, RegistryValueKind.DWord).IsOk.Should().BeFalse("un DWORD reviendrait en REG_SZ");
        Win32RegistryReader.Texte(@"C:\Windows", RegistryValueKind.ExpandString).IsOk.Should()
            .BeFalse("un REG_EXPAND_SZ, déjà développé, reviendrait en REG_SZ figé");
    }
}
