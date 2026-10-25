using DrDxt.Detection.Gpu.Adlx;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Deux lectures lancées ensemble au début du scan : la seconde sortait aussitôt avec « indisponible » pendant que la
// première initialisait encore NVAPI ou ADLX, et sa valeur restait non lue pour ce scan. Chaque appelant attend la vraie
// réponse (même règle que NVML).
public sealed class InitialisationConcurrenteTests
{
    private static async Task LeSecondAttendLaVraieReponse(Func<Func<bool>, Func<bool>> monter)
    {
        using var commencee = new ManualResetEventSlim();
        using var porte = new ManualResetEventSlim();
        var disponible = monter(() => { commencee.Set(); porte.Wait(); return true; });

        var premier = Task.Run(disponible);
        commencee.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("témoin : l'initialisation a commencé");
        var second = Task.Run(disponible);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("le second attend la fin de l'initialisation au lieu de lire « indisponible »");

        porte.Set();
        (await second).Should().BeTrue();
        (await premier).Should().BeTrue();
    }

    [Fact]
    public Task NvApi_le_second_appelant_attend_l_initialisation()
        => LeSecondAttendLaVraieReponse(init =>
        {
            var entree = new NvApiEntry(() => init() ? (int)NvApiStatus.Ok : -1);
            return () => entree.IsAvailable;
        });

    [Fact]
    public Task Adlx_le_second_appelant_attend_l_initialisation()
        => LeSecondAttendLaVraieReponse(init =>
        {
            var adlx = new AdlxNative(() => (init() ? 0 : 1, (IntPtr)1));
            return () => adlx.IsAvailable;
        });
}
