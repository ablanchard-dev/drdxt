using System.Globalization;
using DrDxt.Core;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>Les phrases des mesures suivent la langue de l'App, comme celles du docteur.</summary>
public class MesuresEnAnglaisTests
{
    [Fact]
    public void Le_gain_cumule_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        var total = GainCumule.Additionner([new GainProuve("gpu.core", "banc", 0.05, 0.2)]);

        GainCumule.Phrase(total).Should().StartWith("NOTHING PROVEN");
    }

    [Fact]
    public void Les_pertes_a_l_interface_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        PertesInterface.Classer(new NicErrorDelta(0, 0, 0, 0, 8000, 500, false), TimeSpan.FromSeconds(20)).Explication
            .Should().Be("20 s window: 8000 packets received, 500 sent, zero discarded and zero errors");
    }

    [Fact]
    public void Le_bufferbloat_sans_envoi_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        Bufferbloat.Juger([]).Raison.Should().Be("no upload observed");
    }

    [Fact]
    public void Le_plafond_gpu_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        PlafondGpu.Borner(100).Lisible.Should().Be("Your graphics card is already saturated: lightening the processor gives no extra frame.");
    }
}
