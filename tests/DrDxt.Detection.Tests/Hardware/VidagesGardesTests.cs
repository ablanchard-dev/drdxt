using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>Un vidage plus vieux que la fenêtre ne compte pas : il masquerait les échecs d'écriture récents.</summary>
public sealed class VidagesGardesTests
{
    [Fact]
    public void Seuls_les_vidages_ecrits_dans_la_fenetre_sont_comptes_et_le_dernier_est_date()
    {
        var windows = Directory.CreateTempSubdirectory("drdxt-vidages-").FullName;
        try
        {
            var minidump = Directory.CreateDirectory(Path.Combine(windows, "Minidump")).FullName;
            var maintenant = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(minidump, "ancien.dmp"), "");
            File.SetLastWriteTimeUtc(Path.Combine(minidump, "ancien.dmp"), maintenant.AddDays(-200));
            File.WriteAllText(Path.Combine(minidump, "recent.dmp"), "");
            File.SetLastWriteTimeUtc(Path.Combine(minidump, "recent.dmp"), maintenant.AddDays(-2));
            File.WriteAllText(Path.Combine(windows, "MEMORY.DMP"), "");
            File.SetLastWriteTimeUtc(Path.Combine(windows, "MEMORY.DMP"), maintenant.AddDays(-300));

            var (petits, complet, dernier) = WindowsDumpCreationLog.VidagesGardes(windows, DateTimeOffset.UtcNow.AddDays(-90));

            petits.Should().Be(1);
            complet.Should().BeFalse();
            dernier!.Value.UtcDateTime.Should().BeCloseTo(maintenant.AddDays(-2), TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(windows, true); }
    }
}
