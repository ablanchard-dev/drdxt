using System.Reflection;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// L'App peut tourner en administrateur ; Measurement charge PresentMonAPI2.dll par son nom.
/// Sans restriction, une DLL absente a cote de l'App serait cherchee dans le PATH (dossiers modifiables
/// par l'utilisateur). La recherche est limitee au dossier de l'assembly et a System32.
/// </summary>
public class DllChargeesHorsDuPathTests
{
    [Fact]
    public void Measurement_ne_cherche_ses_DLL_que_pres_de_lui_et_dans_System32()
    {
        var attribut = typeof(DrDxt.Measurement.Cpu.CpuBaselineService).Assembly
            .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        attribut.Should().NotBeNull("Measurement charge PresentMonAPI2.dll dans une App qui peut etre elevee");
        attribut!.Paths.Should().Be(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32);
    }

    [Fact]
    public void PresentMonAPI2_se_charge_toujours_depuis_le_dossier_de_l_assembly()
    {
        // searchPath null : c'est l'attribut de l'assembly qui s'applique, comme pour un DllImport.
        var charge = NativeLibrary.TryLoad("PresentMonAPI2.dll",
            typeof(DrDxt.Measurement.Cpu.CpuBaselineService).Assembly, null, out var h);
        try { charge.Should().BeTrue("la restriction ne doit pas casser la mesure PresentMon livree a cote"); }
        finally { if (charge) NativeLibrary.Free(h); }
    }
}
