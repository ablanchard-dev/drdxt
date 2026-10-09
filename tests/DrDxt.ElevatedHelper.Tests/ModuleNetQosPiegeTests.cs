using System.Diagnostics;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// 25/09/2026. Le plafond d'envoi passe par powershell.exe lance par le helper eleve. PowerShell 5.1
/// met `Documents\WindowsPowerShell\Modules` en tete de PSModulePath, et l'utilisateur y ecrit sans
/// droits : un faux module NetQos pose la etait charge a la place de celui de Windows. Ce test pose
/// un tel module dans un dossier temporaire, en tete du chemin, et lance un vrai PowerShell avec le
/// prefixe du helper. Lecture seule : aucune politique n'est creee.
/// </summary>
public class ModuleNetQosPiegeTests
{
    [Fact]
    public void Un_faux_module_NetQos_en_tete_du_chemin_n_est_pas_execute()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-netqos-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dossier.FullName, "NetQos"));
            File.WriteAllText(Path.Combine(dossier.FullName, "NetQos", "NetQos.psm1"),
                // Ecrit hors du pipeline : un `| Out-Null` du script ne doit pas pouvoir l'avaler.
                "function Get-NetQosPolicy { [Console]::Out.WriteLine('PIEGE-EXECUTE') }");

            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var psi = new ProcessStartInfo(Path.Combine(system32, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["PSModulePath"] = dossier.FullName + ";" + Environment.GetEnvironmentVariable("PSModulePath");
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         Win32NetworkQosActions.ModuleSysteme(system32)
                         + "Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null;"
                         + "'LU-PAR-LE-MODULE-SYSTEME'" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var sortie = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000).Should().BeTrue();

            sortie.Should().NotContain("PIEGE-EXECUTE");
            // Temoin : le script est alle au bout avec le module de Windows.
            sortie.Should().Contain("LU-PAR-LE-MODULE-SYSTEME");
            p.ExitCode.Should().Be(0);
        }
        finally
        {
            dossier.Delete(recursive: true);
        }
    }
}
