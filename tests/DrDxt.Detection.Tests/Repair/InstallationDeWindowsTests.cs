using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 07/10/2026 (D8) : la date de la dernière installation de Windows, en secondes depuis 1970.
public sealed class InstallationDeWindowsTests
{
    [Fact]
    public void La_valeur_du_registre_se_lit_en_date_et_rien_d_autre_ne_passe()
    {
        InstallationDeWindows.Depuis(1744241047).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1744241047));
        InstallationDeWindows.Depuis(0).Should().BeNull();
        InstallationDeWindows.Depuis("10/04/2025").Should().BeNull();
        InstallationDeWindows.Depuis(null).Should().BeNull();
    }

    [Fact]
    public void Tout_windows_a_une_date_d_installation_passee()
        => InstallationDeWindows.Lire().Should().NotBeNull().And.Subject.As<DateTimeOffset>().Should().BeBefore(DateTimeOffset.Now);
}
