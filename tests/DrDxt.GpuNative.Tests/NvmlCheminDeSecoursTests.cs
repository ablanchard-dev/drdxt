using DrDxt.GpuNative.Nvml;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// nvml.dll se charge aussi dans le helper élevé. Un chemin écrit en dur sur « C:\ » pointe, quand Windows est sur un autre
// disque, vers un volume de données où un compte ordinaire crée des dossiers : sa DLL serait chargée en administrateur.
// Le repli suit le vrai dossier Program Files, protégé par Windows.
public sealed class NvmlCheminDeSecoursTests
{
    [Fact]
    public void Windows_sur_un_autre_disque_le_repli_le_suit()
    {
        NvmlNative.CheminDeSecours(@"D:\Program Files").Should().Be(@"D:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll");
    }

    [Fact]
    public void Le_repli_de_nvml_vit_sous_le_vrai_dossier_Program_Files()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        NvmlNative.NvsmiFallback.Should().StartWith(programFiles + Path.DirectorySeparatorChar);
    }
}
