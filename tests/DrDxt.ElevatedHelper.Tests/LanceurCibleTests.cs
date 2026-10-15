using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;
using Xunit;

namespace DrDxt.ElevatedHelper.Tests;

// L'invite Windows porte le lanceur natif quand il est installé à côté du helper.
public sealed class LanceurCibleTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-lanceur-" + Guid.NewGuid().ToString("N"));

    public LanceurCibleTests() => Directory.CreateDirectory(_dossier);
    public void Dispose() => Directory.Delete(_dossier, true);

    [Fact]
    public void Avec_le_lanceur_a_cote_c_est_lui_qui_recoit_l_elevation_et_le_helper_est_son_premier_argument()
    {
        var helper = Path.Combine(_dossier, "DrDxt.ElevatedHelper.exe");
        File.WriteAllText(helper, "");
        File.WriteAllText(Path.Combine(_dossier, RunAsProcessLauncher.Lanceur), "");

        var (fichier, prefixe) = RunAsProcessLauncher.Cible(helper);

        fichier.Should().Be(Path.Combine(_dossier, "DrDxt.Lanceur.exe"));
        prefixe.Should().Equal("DrDxt.ElevatedHelper.exe");
    }

    [Fact]
    public void Sans_lanceur_le_helper_est_lance_directement()
    {
        var helper = Path.Combine(_dossier, "DrDxt.ElevatedHelper.exe");
        File.WriteAllText(helper, "");

        var (fichier, prefixe) = RunAsProcessLauncher.Cible(helper);

        fichier.Should().Be(helper);
        prefixe.Should().HaveCount(0, "le helper reçoit alors l'élévation lui-même");
    }
}
