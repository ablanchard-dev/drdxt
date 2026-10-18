using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Une date WMI sans heure (date du BIOS, date d'un pilote) s'écrit à minuit UTC : la convertir en heure locale la fait
// reculer d'un jour à l'ouest de Greenwich. Là où on la lit, on la lit au jour (InventaireDesPilotes.DateDmtf).
public sealed class DatesSansHeureTests
{
    [Fact]
    public void Une_date_de_BIOS_ou_de_pilote_n_est_jamais_convertie_en_heure_locale()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var lecteurs = Directory.EnumerateFiles(Path.Combine(d!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var s && (s.Contains("\"ReleaseDate\"") || s.Contains("\"DriverDate\"")))
            .ToList();

        // Témoin : le crible a bien trouvé les lecteurs de ces dates (la date du BIOS, l'inventaire des pilotes).
        lecteurs.Select(Path.GetFileName).Should().Contain(["RepairAuditService.cs", "BoardBiosDetector.cs"]).And.NotBeEmpty();
        lecteurs.Where(f => File.ReadAllText(f).Contains("ManagementDateTimeConverter.ToDateTime"))
            .Select(Path.GetFileName).Should().BeEmpty();
    }
}
