using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

public class EtatDuPosteWindowsTests
{
    // GetSystemTimes : le temps noyau inclut le repos. 100 de noyau dont 80 de repos, 0 d'utilisateur : 20 % occupes.
    [Theory]
    [InlineData(80, 100, 0, 20)]
    [InlineData(0, 50, 50, 100)]
    [InlineData(100, 100, 0, 0)]
    [InlineData(30, 60, 40, 70)]
    public void La_charge_retire_le_repos_du_temps_noyau(long repos, long noyau, long utilisateur, double attendu)
        => EtatDuPosteWindows.Charge(repos, noyau, utilisateur).Should().BeApproximately(attendu, 1e-9);

    [Fact]
    public void Un_total_nul_est_une_charge_inconnue()
        => EtatDuPosteWindows.Charge(0, 0, 0).Should().BeNull();
}
