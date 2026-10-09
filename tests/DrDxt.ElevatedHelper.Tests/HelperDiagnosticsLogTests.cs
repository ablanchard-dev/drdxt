using System.Diagnostics;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le journal du helper élevé vit dans le profil de l'utilisateur. Une session non élevée peut y
/// remplacer un dossier par une jonction : l'ajout élevé partirait alors où elle pointe. Ces tests
/// créent une vraie jonction (mklink /J, sans droit particulier).
/// </summary>
public sealed class HelperDiagnosticsLogTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "drdxt-journal-" + Guid.NewGuid().ToString("N"));

    public HelperDiagnosticsLogTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        // Une jonction se retire sans toucher à sa cible ; puis le reste.
        foreach (var d in Directory.GetDirectories(_base))
            if (File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
        Directory.Delete(_base, recursive: true);
    }

    private static void Jonction(string lien, string cible)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", lien, cible } })!;
        p.WaitForExit();
        File.GetAttributes(lien).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("témoin : la jonction existe");
    }

    [Fact]
    public void Par_defaut_le_journal_eleve_vit_dans_la_racine_durcie_et_non_dans_le_profil()
    {
        // Le chemin seul : le constructeur creerait ou reparerait %ProgramData%\DrDxt sur la machine.
        var chemin = HelperDiagnosticsLog.CheminParDefaut();
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDxt");
        Path.GetDirectoryName(chemin).Should().Be(racine);
        chemin.Should().NotStartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [Fact]
    public void Un_dossier_normal_recoit_le_journal()
    {
        var dossier = Path.Combine(_base, "normal");
        new HelperDiagnosticsLog(dossier).Change("t", "a", "b", "test");
        File.Exists(Path.Combine(dossier, "helper.journal.log")).Should().BeTrue();
    }

    [Fact]
    public void Un_dossier_remplace_par_une_jonction_ne_recoit_rien()
    {
        var cible = Path.Combine(_base, "cible");
        Directory.CreateDirectory(cible);
        // Témoin : un fichier déjà là, pour que la liste lue prouve qu'elle voit la cible.
        File.WriteAllText(Path.Combine(cible, "temoin.txt"), "x");
        var lien = Path.Combine(_base, "journal");
        Jonction(lien, cible);

        new HelperDiagnosticsLog(lien).Change("t", "a", "b", "test");

        Directory.GetFiles(cible).Should().HaveCount(1, "seul le témoin : l'écriture élevée ne traverse pas une jonction");
        File.Exists(Path.Combine(cible, "helper.journal.log")).Should().BeFalse();
    }
}
