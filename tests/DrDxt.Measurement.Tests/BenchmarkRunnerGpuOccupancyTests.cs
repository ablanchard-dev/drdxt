using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// L'occupation GPU est appariee, et le repli ne doit jamais se rallumer sur une capture neuve.
///
/// Deux defauts, trouves l'un apres l'autre :
///   1. la serie GPU etait la seule des sept a etre empilee sans appariement, donc une capture
///      desynchronisee alimentait quand meme l'occupation — celle qui porte le verdict de goulot ;
///   2. l'apparier run par run ne suffisait pas. Sur cinq passes dont une seule decalee, le
///      numerateur comptait quatre passes et le denominateur cinq : la part devenait incalculable
///      et `GpuBusyPercent` retombait sur « moyenne sur moyenne », c'est-a-dire sur le melange de
///      populations qu'on venait de retirer — sur une mesure du JOUR.
///
/// L'invariant que ce fichier verrouille : une mesure qui a lu du GPU publie toujours sa part
/// appariee.
/// </summary>
public class BenchmarkRunnerGpuOccupancyTests
{
    /// <param name="gpuBusy">null = PresentMon n'a rien rendu ; longueur differente = serie
    /// desynchronisee, qui ne doit alimenter aucun rapport.</param>
    private static FrameCapture Capture(IReadOnlyList<double> frames, IReadOnlyList<double>? gpuBusy)
    {
        var vide = Array.Empty<double>();
        return new FrameCapture(frames, vide, gpuBusy ?? vide, vide, vide, vide, vide);
    }

    private static IBenchmarkRunner Runner(params FrameCapture[] passes)
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        var file = passes.Select(Result<FrameCapture>.Ok).ToArray();
        var i = 0;
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(_ => file[Math.Min(i++, file.Length - 1)]);
        return new BenchmarkRunner(s);
    }

    private static List<double> Trames(int combien, double ms) => Enumerable.Repeat(ms, combien).ToList();

    [Fact]
    public async Task Une_passe_DESYNCHRONISEE_ne_rebranche_pas_le_repli()
    {
        // Deux passes de 100 trames a 10 ms. La premiere a une serie GPU appariee a 5 ms ; la
        // seconde rend une serie de longueur differente — elle ne doit compter NI en haut, NI en
        // bas.
        var r = await Runner(
            Capture(Trames(100, 10), Trames(100, 5)),
            Capture(Trames(100, 10), Trames(40, 5)))
            .RunAsync(1234, TimeSpan.FromSeconds(1), 2, CancellationToken.None);

        var b = r.Value!;
        b.GpuBusyShare.Should().NotBeNull("une mesure qui a lu du GPU publie TOUJOURS sa part "
                                          + "appariee, sinon le repli se rallume");
        b.GpuBusyShare!.Value.Should().BeApproximately(0.5, 1e-9,
            "seule la passe appariee compte, des deux cotes du rapport");
        b.GpuBusyPercent.Should().BeApproximately(50, 1e-6);
    }

    [Fact]
    public async Task Un_GPU_busy_a_ZERO_reste_dans_le_calcul()
    {
        // La moitie des trames sans travail GPU : mesure valide, et c'est elle que l'ancienne
        // moyenne ecartait du numerateur sans l'ecarter du denominateur.
        var gpu = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.0 : 5.0).ToList();

        var r = await Runner(Capture(Trames(100, 10), gpu))
            .RunAsync(1234, TimeSpan.FromSeconds(1), 1, CancellationToken.None);

        r.Value!.GpuBusyPercent.Should().BeApproximately(25, 1e-6,
            "250 ms de GPU sur 1 000 ms ecoulees ; l'ancienne forme rendait 50 %");
    }

    [Fact]
    public async Task Sans_serie_GPU_du_tout_le_produit_ne_dit_RIEN_de_l_occupation()
    {
        var r = await Runner(Capture(Trames(100, 10), null))
            .RunAsync(1234, TimeSpan.FromSeconds(1), 1, CancellationToken.None);

        r.Value!.GpuBusyShare.Should().BeNull();
        r.Value.GpuBusyPercent.Should().BeNull("« non mesure » ne se remplace pas par un zero, "
                                               + "et surtout pas par une approximation muette");
    }
}
