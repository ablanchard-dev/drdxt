using System.Diagnostics;
using System.Text.RegularExpressions;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 26/09/2026 : le plan d'alimentation actif, sur lequel le levier du plan se décide, était lu par un
// lecteur qu'aucun test n'exerçait sur le vrai système. Il doit rendre le même GUID que powercfg.
public sealed class PowerPlanReaderSurLeVraiSystemeTests
{
    [Fact]
    public void Le_plan_actif_lu_est_celui_que_donne_powercfg()
    {
        var lu = new Win32PowerPlanReader().GetActivePowerPlan();

        using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var sortie = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var attendu = Regex.Match(sortie, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        attendu.Success.Should().BeTrue("temoin : powercfg a rendu un GUID ({0})", sortie.Trim());
        lu.IsOk.Should().BeTrue();
        lu.Value.Should().Be(Guid.Parse(attendu.Value));
    }
}
