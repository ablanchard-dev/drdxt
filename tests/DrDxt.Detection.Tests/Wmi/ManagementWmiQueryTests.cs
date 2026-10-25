using DrDxt.Core;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Wmi;

public class ManagementWmiQueryTests
{
    [Fact]
    public void Invalid_wql_fails_cleanly_without_throwing()
    {
        var sut = new ManagementWmiQuery();

        var act = () => sut.Query("THIS IS NOT WQL");

        var r = act.Should().NotThrow().Subject;
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }

    // winmgmt figé (connexion au scope, exécution ou énumération) : la requête entière rend un échec au délai, au lieu de
    // figer le tour du service (et tous ceux qui attendent derrière lui) jusqu'à son redémarrage.
    [Fact]
    public void Une_requete_que_WMI_ne_rend_jamais_echoue_au_delai()
    {
        using var jamais = new ManualResetEventSlim(false);
        var sut = new ManagementWmiQuery(TimeSpan.FromMilliseconds(200), (_, _) => { jamais.Wait(); return Result<IReadOnlyList<WmiRow>>.Ok([]); });
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        var r = sut.Query("SELECT Name FROM Win32_Processor");

        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiTimeout);
        jamais.Set();
    }

    [Fact]
    public void Le_delai_de_production_est_de_dix_secondes()
        => ManagementWmiQuery.Delai.Should().Be(TimeSpan.FromSeconds(10));

    [Fact]
    public void Real_processor_query_returns_at_least_one_row()
    {
        // Test d'intégration léger : tourne sur tout Windows, sans admin.
        var sut = new ManagementWmiQuery();

        var r = sut.Query("SELECT Name FROM Win32_Processor");

        r.IsOk.Should().BeTrue();
        r.Value!.Should().NotBeEmpty();
    }
}
