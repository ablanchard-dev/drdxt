using DrDxt.Core;
using DrDxt.Detection.Game;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Game;

// Lancé en administrateur, DrDXT ne suit pas un lien posé sans droits : un dossier de réglages du jeu qui passe par l'un d'eux
// ne se lit pas, et l'écran le dit au lieu de ne rien signaler.
public sealed class ReglagesCodLienNonSuiviTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-cod-lien-").FullName;
    public void Dispose()
    {
        // Une jonction se retire seule d'abord : la suppression récursive refuse de la traverser.
        foreach (var d in Directory.GetDirectories(_dossier).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            Directory.Delete(d);
        Directory.Delete(_dossier, true);
    }

    private string Jonction()
    {
        var cible = Path.Combine(_dossier, "vrai-documents");
        var lien = Path.Combine(_dossier, "Documents");
        Directory.CreateDirectory(Path.Combine(cible, "Call of Duty", "players"));
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{lien}\" \"{cible}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        new DirectoryInfo(lien).Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("témoin : la jonction existe");
        return lien;
    }

    [Fact]
    public void Eleve_un_dossier_du_jeu_derriere_un_lien_se_dit_non_lu()
    {
        var lien = Jonction();
        var dossiers = new[] { Path.Combine(lien, "Call of Duty", "players") };

        ReglagesCod.LienNonSuivi(eleve: true, dossiers).Should().Be(lien);
        ReglagesCod.ConstatNonSuivi(lien).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Sans_elevation_ou_sans_lien_rien_n_est_dit()
    {
        var lien = Jonction();

        ReglagesCod.LienNonSuivi(eleve: false, [Path.Combine(lien, "Call of Duty", "players")]).Should().BeNull();
        ReglagesCod.LienNonSuivi(eleve: true, [Path.Combine(_dossier, "vrai-documents", "Call of Duty", "players")]).Should().BeNull();
    }
}
