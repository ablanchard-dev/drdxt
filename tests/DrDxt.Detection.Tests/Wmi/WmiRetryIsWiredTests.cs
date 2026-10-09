using DrDxt.Detection.Wmi;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Detection.Tests.Wmi;

/// <summary>
/// Le rejeu ne vaut que s'il est BRANCHE. Un decorateur enregistre nulle part est du code
/// mort qui se lit comme une garantie : ce test pinne le cablage, pas l'intention.
/// </summary>
public class WmiRetryIsWiredTests
{
    [Fact]
    public void Detection_resout_un_IWmiQuery_qui_rejoue()
    {
        var provider = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();

        provider.GetRequiredService<IWmiQuery>().Should().BeOfType<RetryingWmiQuery>();
    }
}
