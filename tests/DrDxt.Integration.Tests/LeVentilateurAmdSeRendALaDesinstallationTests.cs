using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// La désinstallation rend le ventilateur NVIDIA à l'automatique (le bail du helper tué ne le rend plus). Sur une Radeon, la
// vitesse fixe reste fermée tant qu'aucune architecture AMD n'a son rapport : DrDXT n'a rien pu y poser, et rendre
// l'automatique écraserait la courbe que le joueur s'est faite dans le logiciel AMD. Le jour où un rapport ouvre une
// architecture AMD, ce test rougit tant que la désinstallation ne passe pas aussi par ADLX.
public sealed class LeVentilateurAmdSeRendALaDesinstallationTests
{
    private static readonly GpuArchitecture[] Amd =
        [GpuArchitecture.Gcn, GpuArchitecture.Rdna1, GpuArchitecture.Rdna2, GpuArchitecture.Rdna3, GpuArchitecture.Rdna4];

    [Fact]
    public void Une_architecture_AMD_ouverte_exige_le_rendu_du_ventilateur_par_ADLX_a_la_desinstallation()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var mode = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DrDxt.ElevatedHelper", "ModeDesinstallation.cs"));

        if (GpuTuningCoverage.ValideesEnVie.Overlaps(Amd))
            mode.Should().Contain("VentilateurSelonLaCarte",
                "une Radeon validée peut porter une vitesse fixe que le bail tué ne rend plus : la désinstallation doit la rendre");
        else
            mode.Should().NotContain("VentilateurSelonLaCarte",
                "témoin : sans Radeon validée, la désinstallation ne touche pas à la courbe AMD du joueur");
    }
}
