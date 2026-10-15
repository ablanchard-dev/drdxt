using System.IO;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// CONFIDENTIALITE.md promet ce que « Exporter ma machine » retire. La liste y est recopiée mot pour
/// mot depuis <see cref="MachineExportee.CeQuiEstRetire"/> : une anonymisation qui change sans la politique fait échouer ici.
/// </summary>
public sealed class PolitiqueDeConfidentialiteTests
{
    private static string Politique()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "CONFIDENTIALITE.md"));
    }

    [Fact]
    public void Chaque_retrait_de_l_export_est_ecrit_dans_la_politique()
        => MachineExportee.CeQuiEstRetire.Where(r => !Politique().Contains("- " + r + "\n", StringComparison.Ordinal)
                                                     && !Politique().Contains("- " + r + "\r\n", StringComparison.Ordinal))
            .Should().BeEmpty("CONFIDENTIALITE.md recopie la liste de MachineExportee.CeQuiEstRetire");

    [Fact]
    public void La_politique_renvoie_a_ce_qui_quitte_la_machine()
        => Politique().Should().Contain("« Ce qui quitte la machine »");
}
