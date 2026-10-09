using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// 27/09/2026 : le service de demarrage (case de l'installeur) decide ce qu'il reapplique en lisant le
// registre, le plan d'alimentation et l'etat des services. Ces trois lecteurs n'etaient exerces que sur
// des doublures. Chacun est confronte ici a l'outil de Windows qui lit la meme chose (reg, powercfg, sc).
// Lecture seule ; ces outils existent sur tout Windows, runner de CI compris.
public sealed class LecteursDuServiceSurLeVraiSystemeTests
{
    private const string VersionWindows = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static string Lancer(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var sortie = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return sortie;
    }

    private static string RegQuery(string nom)
    {
        var sortie = Lancer("reg.exe", $"query \"HKLM\\{VersionWindows}\" /v {nom}");
        var m = Regex.Match(sortie, $@"^\s*{nom}\s+REG_\w+\s+(.+?)\s*$", RegexOptions.Multiline);
        m.Success.Should().BeTrue("temoin : reg a rendu la valeur ({0})", sortie.Trim());
        return m.Groups[1].Value;
    }

    [Fact]
    public void Une_chaine_du_registre_se_lit_comme_reg_la_lit()
    {
        var lu = new Win32RegistryValueReader().ReadCanonical(
            new RegistryDescriptor(RegistryHiveKind.LocalMachine, VersionWindows, "CurrentBuildNumber", RegistryValueType.String));

        lu.IsOk.Should().BeTrue();
        lu.Value.Should().Be(RegQuery("CurrentBuildNumber"));
    }

    [Fact]
    public void Un_dword_du_registre_se_lit_comme_reg_le_lit()
    {
        var lu = new Win32RegistryValueReader().ReadCanonical(
            new RegistryDescriptor(RegistryHiveKind.LocalMachine, VersionWindows, "UBR", RegistryValueType.DWord));

        var hex = RegQuery("UBR");
        lu.IsOk.Should().BeTrue();
        lu.Value.Should().Be(uint.Parse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Le_plan_actif_se_lit_comme_powercfg_le_lit()
    {
        var sortie = Lancer("powercfg.exe", "/getactivescheme");
        var m = Regex.Match(sortie, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        m.Success.Should().BeTrue("temoin : powercfg a rendu un GUID ({0})", sortie.Trim());

        var lu = new PInvokePowerPlanReader().ReadActiveSchemeCanonical();

        lu.IsOk.Should().BeTrue();
        lu.Value.Should().Be(ReconcilableValueCanonicalizer.PowerPlan(Guid.Parse(m.Value)));
    }

    [Fact]
    public void L_etat_d_un_service_se_lit_comme_sc_le_lit()
    {
        // Le journal d'evenements tourne sur tout Windows demarre.
        var sortie = Lancer("sc.exe", "query EventLog");
        sortie.Should().Contain("RUNNING", "temoin : sc voit le service demarre");

        new ServiceControllerStateReader().ReadStateCanonical("EventLog").Value.Should().Be("running");
    }

    [Fact]
    public void Un_service_absent_se_lit_absent_comme_sc_le_dit()
    {
        const string absent = "DrDxtServiceQuiNExistePas";
        Lancer("sc.exe", $"query {absent}").Should().Contain("1060", "temoin : sc dit que le service n'existe pas");

        var lu = new ServiceControllerStateReader().ReadStateCanonical(absent);

        lu.IsOk.Should().BeTrue("absent est un etat, pas une panne");
        lu.Value.Should().Be("notinstalled");
    }
}
