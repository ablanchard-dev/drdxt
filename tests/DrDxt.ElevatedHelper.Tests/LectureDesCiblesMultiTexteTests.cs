using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// La désinstallation ne rend une feuille que si la valeur relue est celle que DrDXT a posée. Un REG_MULTI_SZ (PagingFiles)
// se lisait « illisible » : le fichier d'échange déplacé par DrDXT ne revenait jamais à sa place.
public sealed class LectureDesCiblesMultiTexteTests
{
    [Fact]
    public void Un_REG_MULTI_SZ_se_relit_sous_la_forme_que_l_ecriture_pose()
        => LectureDesCiblesWin32.Forme(RegistryValueType.MultiString, "PagingFiles", new[] { @"D:\pagefile.sys 0 0" })
            .Value.Should().Be(@"D:\pagefile.sys 0 0");

    [Fact]
    public void Plusieurs_entrees_se_relisent_une_par_ligne_sans_les_vides()
        => LectureDesCiblesWin32.Forme(RegistryValueType.MultiString, "PagingFiles",
                new[] { @" C:\pagefile.sys 0 0 ", "", @"D:\pagefile.sys 1024 4096" })
            .Value.Should().Be("C:\\pagefile.sys 0 0\nD:\\pagefile.sys 1024 4096");

    [Fact]
    public void Un_type_inattendu_reste_illisible()
        => LectureDesCiblesWin32.Forme(RegistryValueType.MultiString, "PagingFiles", 7).IsOk.Should().BeFalse();

    // Une origine d'un autre type que la pose ne se rend pas : réécrite sous le type posé, un REG_EXPAND_SZ deviendrait un
    // REG_SZ (ses variables ne seraient plus développées), une valeur binaire un nombre.
    [Theory]
    [InlineData(RegistryValueType.String, Microsoft.Win32.RegistryValueKind.String, true)]
    [InlineData(RegistryValueType.String, Microsoft.Win32.RegistryValueKind.ExpandString, false)]
    [InlineData(RegistryValueType.DWord, Microsoft.Win32.RegistryValueKind.DWord, true)]
    [InlineData(RegistryValueType.DWord, Microsoft.Win32.RegistryValueKind.QWord, false)]
    [InlineData(RegistryValueType.DWord, Microsoft.Win32.RegistryValueKind.Binary, false)]
    [InlineData(RegistryValueType.QWord, Microsoft.Win32.RegistryValueKind.QWord, true)]
    [InlineData(RegistryValueType.MultiString, Microsoft.Win32.RegistryValueKind.MultiString, true)]
    [InlineData(RegistryValueType.MultiString, Microsoft.Win32.RegistryValueKind.String, false)]
    public void Le_type_lu_doit_etre_le_type_pose(RegistryValueType pose, Microsoft.Win32.RegistryValueKind lu, bool conforme)
        => LectureDesCiblesWin32.TypeConforme(pose, lu).Should().Be(conforme);
}
