using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>La sonde du pilote, lue par le helper dans le commit du manifeste : un winmgmt figé ne fige pas le commit.</summary>
public sealed class SondeDePiloteWmiTests
{
    [Fact]
    public void Une_lecture_que_WMI_ne_rend_jamais_echoue_au_delai()
    {
        using var jamais = new ManualResetEventSlim(false);
        var sonde = new SondeDePiloteWmi(_ => { jamais.Wait(); return Result<string?>.Ok("32.0.15.6094"); }, TimeSpan.FromMilliseconds(200));
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        var r = sonde.ReadDriverVersion("NVIDIA");

        // La borne prouve que l'appel revient (sans délai, il ne reviendrait jamais), pas la durée du délai : large,
        // elle tient sur un runner de CI à deux cœurs saturé par les autres projets de tests.
        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiTimeout);
        jamais.Set();
    }

    [Fact]
    public void Une_lecture_qui_repond_rend_sa_valeur()
        => new SondeDePiloteWmi(_ => Result<string?>.Ok("32.0.15.6094"), TimeSpan.FromSeconds(5)).ReadDriverVersion("NVIDIA")
            .Value.Should().Be("32.0.15.6094");
}
