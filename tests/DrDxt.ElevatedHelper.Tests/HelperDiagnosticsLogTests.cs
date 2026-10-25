using System.Diagnostics;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le journal du helper élevé vit dans la racine durcie ; un dossier passé à la main (tests, surcharge) peut, lui, être
/// remplacé par une jonction, et un nom posé d'avance en lien dur. L'ajout comme l'export ne suivent alors aucun lien. Ces
/// tests créent une vraie jonction (mklink /J, sans droit particulier).
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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lien, string cible, IntPtr securite);

    // La réparation de la racine ne descend pas : un journal posé d'avance en lien dur dans un %ProgramData%\DrDxt préparé
    // par un compte faisait écrire le helper élevé dans un autre fichier. L'ajout n'écrit que dans un fichier à un seul nom.
    [Fact]
    public void Un_journal_pose_d_avance_en_lien_dur_ne_fait_jamais_ecrire_ailleurs()
    {
        var dossier = Directory.CreateDirectory(Path.Combine(_base, "racine")).FullName;
        var temoin = Path.Combine(_base, "temoin.txt");
        File.WriteAllText(temoin, "intact");
        CreateHardLink(Path.Combine(dossier, "helper.journal.log"), temoin, IntPtr.Zero).Should().BeTrue();

        new HelperDiagnosticsLog(dossier).Change("t", "a", "b", "test");

        File.ReadAllText(temoin).Should().Be("intact");
    }

    // L'export lit le journal en administrateur et en rend le texte à l'App, qui ne l'est pas : il ne suit pas plus un lien
    // que l'ajout. Un nom posé d'avance en lien dur, ou un dossier remplacé par une jonction, ne lui fait rien lire d'autre.
    [Fact]
    public async Task L_export_ne_lit_pas_un_autre_fichier_par_un_lien_dur()
    {
        var dossier = Directory.CreateDirectory(Path.Combine(_base, "racine")).FullName;
        var secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(secret, "secret");
        CreateHardLink(Path.Combine(dossier, "helper.journal.log"), secret, IntPtr.Zero).Should().BeTrue();

        var r = await new HelperDiagnosticsLog(dossier).ExportAsync();

        (r.IsOk ? r.Value : "").Should().NotContain("secret");
    }

    [Fact]
    public async Task L_export_ne_lit_pas_a_travers_une_jonction()
    {
        var cible = Directory.CreateDirectory(Path.Combine(_base, "cible")).FullName;
        File.WriteAllText(Path.Combine(cible, "helper.journal.log"), "secret");
        var lien = Path.Combine(_base, "journal");
        Jonction(lien, cible);

        var r = await new HelperDiagnosticsLog(lien).ExportAsync();

        (r.IsOk ? r.Value : "").Should().NotContain("secret");
    }

    [Fact]
    public async Task L_export_rend_le_journal_ecrit()
    {
        var dossier = Path.Combine(_base, "normal");
        var journal = new HelperDiagnosticsLog(dossier);
        journal.Change("t", "a", "b", "test");

        (await journal.ExportAsync()).Value.Should().Contain("tweak=t");
    }
}
