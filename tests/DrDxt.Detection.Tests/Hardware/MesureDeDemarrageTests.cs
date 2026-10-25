using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Un événement du journal des performances sans sa durée n'est pas une mesure : un démarrage à 0 ms entrerait dans la
// moyenne et ferait croire à un démarrage éclair.
public sealed class MesureDeDemarrageTests
{
    private static readonly DateTimeOffset Quand = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_demarrage_sans_duree_n_est_pas_une_mesure()
    {
        WindowsBootPerformanceLog.Mesure(100, Quand, new Dictionary<string, string>()).Should().BeNull();
        WindowsBootPerformanceLog.Mesure(101, Quand, new Dictionary<string, string> { ["Name"] = "x.exe" }).Should().BeNull();
    }

    [Fact]
    public void Une_duree_lue_est_une_mesure()
    {
        WindowsBootPerformanceLog.Mesure(100, Quand, new Dictionary<string, string> { ["BootTime"] = "21000" })!.DureeMs.Should().Be(21000);
        var app = WindowsBootPerformanceLog.Mesure(101, Quand,
            new Dictionary<string, string> { ["FriendlyName"] = "Steam", ["TotalTime"] = "1800", ["DegradationTime"] = "900" })!;
        app.Nom.Should().Be("Steam");
        app.DegradationMs.Should().Be(900);
    }
}
