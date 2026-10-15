using DrDxt.Core;
using DrDxt.Detection.Scoring;
using DrDxt.Measurement;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Le meme trou que le p95, sur les autres grandeurs optionnelles du banc.
///
/// Chaque argument optionnel que <see cref="BenchmarkRunner"/> (le banc de production) passe a
/// `BenchmarkResult` doit etre garde par un test : force a `null` par mutation, sans test qui le lit,
/// la suite resterait verte (mesure : 8 mutations sur 15 vertes). Ce fichier ferme les grandeurs qui
/// ont un consommateur.
///
/// Ce que ces tests prouvent, et pas autre chose. Les etats du classifieur sont deja testes
/// sur des `BenchmarkResult` construits a la main (`BottleneckClassifierTests`). Ce qui n'etait
/// teste nulle part, c'est le chemin : que le banc de production remplisse bien la grandeur que
/// ces etats lisent. Chaque test part donc de `BenchmarkRunner` et va jusqu'au consommateur qui
/// se tait quand la grandeur manque.
/// </summary>
public class LeBancRenseigneCeQueLAvalLitTests
{
    // ── La passe de reference ───────────────────────────────────────────────────────────────
    // 200 trames a 8 ms : cadence parfaitement reguliere (CV = 0, p99/p50 = 1), donc aucun etat
    // de cadence ne peut se declencher par les frametimes. Ce qui declenche un etat dans ces
    // tests ne peut venir que de la serie annexe qu'on fait varier — sans ca, un test vert ne
    // dirait pas QUELLE grandeur l'a fait passer.
    private const int Trames = 200;
    private const double FrametimeMs = 8.0;

    // GPU-busy 6,24 ms sur 8 ms = 78 % : dans la bande que le classifieur refuse de nommer
    // (0,75–0,80), et attente GPU a 10 % — sous le plancher CPU-bound de 20 %. Aucune branche de
    // « qui pace la frame » ne mord, donc le verdict vient de l'etape qu'on teste, jamais du GPU.
    private const double GpuBusyMs = 6.24;
    private const double GpuWaitMs = 0.8;
    private const double CpuBusyMs = 1.0;

    private static List<double> Constante(double v) => Enumerable.Repeat(v, Trames).ToList();

    private static async Task<BenchmarkResult> Passe(
        double presentApiMs = 0.5,
        double animationErrorMs = 0.0,
        double latenceMs = 1.0,
        double? commit = null,
        IForegroundProbe? focus = null,
        IHardwareProbe? materiel = null,
        TaskCompletionSource? porte = null)
    {
        var capture = new FrameCapture(
            Constante(FrametimeMs),
            Constante(latenceMs),
            Constante(GpuBusyMs),
            Constante(GpuWaitMs),
            Constante(CpuBusyMs),
            Constante(presentApiMs),
            Constante(animationErrorMs));

        // Les sondes de focus et de materiel echantillonnent pendant la capture, toutes les
        // 250 ms. Avec une capture instantanee — ce qu'est une capture substituee — elles
        // peuvent n'etre lues aucune fois : la course existe, et un `Task.Delay` dans le test
        // la rendrait seulement moins probable. La porte la supprime : la capture ne rend la
        // main qu'une fois la sonde lue, sans faire attendre personne.
        async Task<Result<FrameCapture>> Capturer()
        {
            if (porte is not null) await porte.Task.ConfigureAwait(false);
            return Result<FrameCapture>.Ok(capture);
        }

        var session = Substitute.For<IPresentMonSession>();
        session.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        session.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => Capturer());

        var r = await new BenchmarkRunner(session, commitPressure: () => commit,
                foreground: focus, materiel: materiel)
            .RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);

        r.IsOk.Should().BeTrue();
        return r.Value!;
    }

    /// <summary>le plus grave des sept. Sans <c>CommitPressure</c>, une machine au bord de sa
    /// limite de validation — celle qui GELE, que Microsoft documente comme telle — ressort d'un
    /// diagnostic avec « aucun goulot nomme », et le produit lui propose un overclock.</summary>
    [Fact]
    public async Task Une_machine_SOUS_PRESSION_DE_VALIDATION_est_nommee_depuis_le_banc_de_production()
    {
        var mesure = await Passe(commit: 0.95);

        BottleneckClassifier.Classify(mesure).State
            .Should().Be(BottleneckState.RamCommitPressure,
                "sans la pression de validation, la machine qui gele ressort « rien de nomme »");
    }

    /// <summary>Sans <c>AnimationErrorMsP95</c>, l'etat `FramePacing` devient INATTEIGNABLE sur
    /// une machine dont les frametimes sont reguliers — exactement le cas qu'il existe pour
    /// attraper : 125 FPS parfaitement cadences mais desynchronises de la simulation.</summary>
    [Fact]
    public async Task Une_cadence_D_ANIMATION_cassee_est_nommee_depuis_le_banc_de_production()
    {
        // 6 ms > le seuil de 4 ms, alors que le CV des frametimes est nul : c'est la seule
        // grandeur qui peut declencher cet etat sur cette passe.
        var mesure = await Passe(animationErrorMs: 6.0);

        BottleneckClassifier.Classify(mesure).State
            .Should().Be(BottleneckState.FramePacing);
    }

    /// <summary>Sans <c>RenderPresentLatencyMsP95</c>, l'etat `LatencyOnly` ne peut jamais sortir :
    /// une machine dont le debit va mais dont l'image met deux trames a sortir s'entend repondre
    /// qu'il n'y a rien a dire.</summary>
    [Fact]
    public async Task Une_LATENCE_haute_sur_un_debit_sain_est_nommee_depuis_le_banc_de_production()
    {
        // 20 ms de latence p95 pour un frametime median de 8 ms : 2,5x, au-dela du facteur 2.
        var mesure = await Passe(latenceMs: 20.0);

        BottleneckClassifier.Classify(mesure).State
            .Should().Be(BottleneckState.LatencyOnly);
    }

    /// <summary>Les deux comptes d'echantillons. Ils ne portent aucune valeur : ils decident si
    /// le produit a le droit de PARLER d'un p95. A <c>null</c>, les appelants font `?? 0`, et
    /// `TailStatistic` repond « pas interpretable » — le produit se tait pour toujours, sur une
    /// capture pourtant complete. Le silence est le comportement voulu quand le compte manque
    /// Vraiment ; il devient un defaut quand le banc ne l'a jamais rempli.</summary>
    [Fact]
    public async Task Les_comptes_qui_AUTORISENT_le_produit_a_parler_d_un_p95_viennent_du_banc()
    {
        var mesure = await Passe(presentApiMs: 0.5, animationErrorMs: 1.0);

        TailStatistic.PourquoiPasInterpretable(mesure.AnimationErrorSamples ?? 0)
            .Should().BeNull("sans ce compte, l'ecran se tait sur la cadence d'une capture complete");
        TailStatistic.PourquoiPasInterpretable(mesure.PresentStallSamples ?? 0)
            .Should().BeNull("sans ce compte, l'ecran se tait sur l'attente dans Present()");
    }

    /// <summary>Le focus observe. A <c>null</c>, `GameOptimizationSession` ne peut plus ecarter une
    /// passe alt-tabbee : `PasseComparable` rend vrai sur un Unknown, par choix — Unknown ne
    /// condamne pas. La garde ne se declenche donc jamais, et une passe amputee est comparee a une
    /// passe intacte.</summary>
    [Fact]
    public async Task Le_focus_OBSERVE_remonte_du_banc_jusqu_a_la_garde_qui_s_en_sert()
    {
        var porte = new TaskCompletionSource();
        var sonde = Substitute.For<IForegroundProbe>();
        // 9999 : une autre fenetre est devant, le jeu ne l'est pas.
        sonde.ForegroundProcessId().Returns(_ => { porte.TrySetResult(); return 9999; });

        var mesure = await Passe(focus: sonde, porte: porte);

        mesure.ForegroundFraction.Should().NotBeNull(
            "un focus non remonte rend la garde d'alt-tab inoperante, en silence");
        mesure.ForegroundFraction.Should().BeLessThan(FocusObservation.FractionMinimale,
            "cette passe s'est passee derriere une autre fenetre, et la garde doit pouvoir le voir");
    }

    /// <summary>L'observation materielle. Jamais une cause, mais sans elle le produit ne peut pas
    /// dire ce que la carte a fait pendant la passe, et `JugementApply` perd son contexte.</summary>
    [Fact]
    public async Task Ce_que_le_materiel_a_fait_pendant_la_passe_remonte_du_banc()
    {
        var porte = new TaskCompletionSource();
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(_ =>
        {
            porte.TrySetResult();
            return new HardwareReading(GpuPowerW: 180.0, GpuTemperatureC: 68.0);
        });

        var mesure = await Passe(materiel: sonde, porte: porte);

        mesure.Materiel.Should().NotBeNull();
        mesure.Materiel!.Echantillons.Should().BeGreaterThan(0);
    }
}
