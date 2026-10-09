using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La timeline commune : « a CET instant, que faisait la machine ? »
///
/// Jusqu'ici DrDXT correlait « la frame 872 avec la valeur 872 » — par INDEX. C'est ce qui rend
/// impossible de fusionner des producteurs qui n'echantillonnent pas au meme rythme : les frames
/// arrivent a chaque presentation, la telemetrie GPU toutes les N ms, les DPC/ISR par rafales.
/// Trois cadences, aucune alignee sur un index.
///
/// Ces tests protegent deux choses, et la seconde compte plus que la premiere :
///   1. le rapprochement trouve bien le plus proche voisin ;
///   2. il refuse de rapprocher au-dela d'une tolerance — une valeur prise 500 ms plus loin ne
///      decrit pas le meme instant, et la mettre la serait inventer une mesure.
/// </summary>
public class PerformanceTimelineTests
{
    private const long Ms = 1_000_000;

    private static TimelineSample Frame(long ms, double frameTimeMs) =>
        new(ms * Ms, FrameTimeMs: frameTimeMs);

    private static TimelineSample Telemetrie(long ms, double watts, double tempC) =>
        new(ms * Ms, GpuPowerW: watts, GpuTemperatureC: tempC);

    [Fact]
    public void Chaque_frame_recoit_la_telemetrie_du_MEME_instant()
    {
        var frames = new[] { Frame(0, 7.0), Frame(10, 7.1), Frame(20, 6.9) };
        var telem = new[] { Telemetrie(1, 110, 70), Telemetrie(11, 112, 71), Telemetrie(21, 115, 72) };

        var t = PerformanceTimeline.Fusionner(frames, telem);

        t.Select(s => s.GpuPowerW).Should().Equal(110, 112, 115);
        // La reference garde ce qu'elle porte : la fusion complete, elle ne remplace pas.
        t.Select(s => s.FrameTimeMs).Should().Equal(7.0, 7.1, 6.9);
    }

    [Fact]
    public void Le_plus_proche_peut_etre_APRES_la_frame_pas_seulement_avant()
    {
        // Prendre systematiquement l'echantillon precedent decalerait toute la timeline d'un pas.
        var frames = new[] { Frame(10, 7.0) };
        var telem = new[] { Telemetrie(1, 100, 60), Telemetrie(11, 200, 80) };

        PerformanceTimeline.Fusionner(frames, telem)[0].GpuPowerW.Should().Be(200);
    }

    [Fact]
    public void Au_dela_de_la_TOLERANCE_rien_n_est_rempli()
    {
        // Le test qui compte. Le capteur n'a rien dit pendant 500 ms : la frame reste sans
        // telemetrie. Y coller la valeur la plus proche fabriquerait une mesure qui n'existe pas.
        var frames = new[] { Frame(500, 7.0) };
        var telem = new[] { Telemetrie(0, 110, 70) };

        var t = PerformanceTimeline.Fusionner(frames, telem);

        t[0].GpuPowerW.Should().BeNull("« je n'ai pas regarde a cet instant » n'est pas une valeur");
        t[0].FrameTimeMs.Should().Be(7.0);
    }

    [Fact]
    public void Une_valeur_DEJA_mesuree_n_est_jamais_ecrasee()
    {
        // La reference est la source de ce qu'elle porte : PresentMon connait le GPU busy de SA
        // frame mieux qu'un echantillonneur externe.
        var frames = new[] { new TimelineSample(0, FrameTimeMs: 7.0, GpuBusyMs: 6.5) };
        var telem = new[] { new TimelineSample(0, GpuBusyMs: 99.0, GpuPowerW: 110) };

        var t = PerformanceTimeline.Fusionner(frames, telem);

        t[0].GpuBusyMs.Should().Be(6.5);
        t[0].GpuPowerW.Should().Be(110, "les champs VIDES, eux, se completent");
    }

    [Fact]
    public void Sans_telemetrie_du_tout_la_reference_traverse_intacte()
    {
        var frames = new[] { Frame(0, 7.0), Frame(10, 7.1) };

        PerformanceTimeline.Fusionner(frames, Array.Empty<TimelineSample>())
            .Should().BeEquivalentTo(frames);
    }

    [Fact]
    public void La_COUVERTURE_dit_quelle_part_a_vraiment_ete_completee()
    {
        // Une correlation calculee sur 4 % des frames ne vaut rien, et rien d'autre ne le dirait.
        var frames = new[] { Frame(0, 7.0), Frame(1000, 7.1), Frame(2000, 7.2), Frame(3000, 7.3) };
        var telem = new[] { Telemetrie(0, 110, 70) };

        var t = PerformanceTimeline.Fusionner(frames, telem);

        PerformanceTimeline.Couverture(t, s => s.GpuPowerW).Should().Be(0.25);
    }

    [Fact]
    public void Une_longue_session_se_fusionne_sans_exploser()
    {
        // 120 000 frames (dix minutes a 200 fps) contre 6 000 echantillons : un rapprochement par
        // recherche complete serait quadratique. Le parcours doit rester lineaire.
        var frames = Enumerable.Range(0, 120_000).Select(i => Frame(i * 5, 5.0)).ToList();
        var telem = Enumerable.Range(0, 6_000).Select(i => Telemetrie(i * 100, 110, 70)).ToList();

        var t = PerformanceTimeline.Fusionner(frames, telem);

        t.Should().HaveCount(120_000);
        PerformanceTimeline.Couverture(t, s => s.GpuPowerW).Should().BeGreaterThan(0.3);
    }
}
