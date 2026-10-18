using System.Diagnostics;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le point de restauration passe par powershell.exe lancé par le helper élevé, sans invite. PowerShell 5.1 met
/// <c>Documents\WindowsPowerShell\Modules</c> en tête de PSModulePath, et l'utilisateur y écrit sans droits : un module
/// qui y exporte Checkpoint-Computer passait avant celui de Windows (relevé sur la machine de référence : le témoin
/// tournait). Ces tests posent un tel module dans un dossier temporaire et lancent un vrai PowerShell. Lecture seule :
/// la dernière commande demande seulement le chemin des modules ou d'où vient Checkpoint-Computer, aucun point n'est créé.
/// </summary>
public class ModuleCheckpointPiegeTests
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Fact]
    public void L_environnement_du_processus_ne_garde_que_les_modules_de_Windows()
    {
        var psi = Win32RestorePointActions.InfosCheckpoint("DrDXT", System32);

        psi.Environment["PSModulePath"].Should().Be(Path.Combine(System32, @"WindowsPowerShell\v1.0\Modules"));
        psi.FileName.Should().Be(Path.Combine(System32, @"WindowsPowerShell\v1.0\powershell.exe"));
        psi.UseShellExecute.Should().BeFalse("sinon l'environnement fixé n'est pas transmis");
    }

    [Fact]
    public void PowerShell_ne_remet_pas_Documents_dans_le_chemin_fixe()
    {
        // Mesuré sur la machine de référence : sans chemin fixé, Documents passe en tête ; fixé, PowerShell n'y ajoute
        // que Program Files (réservé aux administrateurs).
        var (sortie, code) = Lancer(Win32RestorePointActions.InfosCheckpoint("DrDXT", System32), avecPiegeEnTete: false,
            commande: "$env:PSModulePath");

        sortie.Should().NotContain(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        sortie.Should().NotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\");
        sortie.Should().Contain(Path.Combine(System32, @"WindowsPowerShell\v1.0\Modules"));
        code.Should().Be(0);
    }

    [Fact]
    public void Meme_piege_en_tete_du_chemin_le_module_importe_par_son_chemin_gagne()
    {
        var psi = Win32RestorePointActions.InfosCheckpoint("DrDXT", System32);
        var (sortie, code) = Lancer(psi, avecPiegeEnTete: true,
            commande: Win32RestorePointActions.ModuleSysteme(System32) + "(Get-Command Checkpoint-Computer).Module.Path");

        sortie.Should().NotContain("drdxt-ckpt-");
        sortie.Should().Contain(@"\Microsoft.PowerShell.Management\");
        code.Should().Be(0);
    }

    private static (string Sortie, int Code) Lancer(ProcessStartInfo psi, bool avecPiegeEnTete, string commande)
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-ckpt-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dossier.FullName, "Piege"));
            File.WriteAllText(Path.Combine(dossier.FullName, "Piege", "Piege.psm1"),
                "function Checkpoint-Computer { [Console]::Out.WriteLine('PIEGE-EXECUTE') }");
            if (avecPiegeEnTete) psi.Environment["PSModulePath"] = dossier.FullName + ";" + psi.Environment["PSModulePath"];
            psi.ArgumentList[^1] = commande;

            using var p = Process.Start(psi)!;
            var sortie = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000).Should().BeTrue();
            return (sortie, p.ExitCode);
        }
        finally
        {
            dossier.Delete(recursive: true);
        }
    }
}
