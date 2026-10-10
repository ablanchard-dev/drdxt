using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

public class PresentMonSessionCaptureTests
{
    private const uint Pid = 42;
    private const nint Session = 1;
    private const nint Query = 2;

    // Deterministic clock: each read of Elapsed advances time by a big step, so any
    // duration-bounded loop terminates within a couple of iterations without sleeping.
    private sealed class FakeClock : IMonotonicClock
    {
        private long _ms;
        public TimeSpan Elapsed
        {
            get { var t = TimeSpan.FromMilliseconds(_ms); _ms += 50; return t; }
        }
        public void Restart() => _ms = 0;
    }

    // Configures a fake api whose ConsumeFrames yields exactly the given frametimes on the
    // first poll, then 0 frames thereafter. Frametime is at dataOffset 0, latency at offset 8.
    private static IPresentMonApi ApiYielding(double[] frametimes, uint blobSize = 24,
        ulong frametimeOffset = 0, ulong latencyOffset = 8, double[]? latencies = null,
        ulong gpuBusyOffset = 16, double[]? gpuBusy = null, int[]? presentModes = null, ulong presentModeSize = 0,
        double[]? displayLatencies = null, ulong displayLatencyOffset = 32)
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.RegisterFrameQuery(Session, Arg.Any<PM_QUERY_ELEMENT[]>(), out Arg.Any<nint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                // Fill dataOffset on the elements like the native call would.
                var elements = (PM_QUERY_ELEMENT[])c[1];
                if (elements.Length > 0) elements[0].dataOffset = frametimeOffset;
                if (elements.Length > 1) elements[1].dataOffset = latencyOffset;
                if (elements.Length > 2) elements[2].dataOffset = gpuBusyOffset;
                if (elements.Length > 7) { elements[7].dataOffset = 24; elements[7].dataSize = presentModeSize; }
                // B4 : la latence d'affichage n'est livrée (taille 8) que si le test la donne.
                if (elements.Length > 9 && displayLatencies is not null) { elements[9].dataOffset = displayLatencyOffset; elements[9].dataSize = 8; }
                c[2] = Query;
                c[3] = blobSize;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });

        var firstPoll = true;
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var buf = (byte[])c[2];
                if (firstPoll)
                {
                    firstPoll = false;
                    for (var i = 0; i < frametimes.Length; i++)
                    {
                        BitConverter.GetBytes(frametimes[i]).CopyTo(buf, i * (int)blobSize + (int)frametimeOffset);
                        var lat = latencies is not null && i < latencies.Length ? latencies[i] : 0.0;
                        BitConverter.GetBytes(lat).CopyTo(buf, i * (int)blobSize + (int)latencyOffset);
                        var gb = gpuBusy is not null && i < gpuBusy.Length ? gpuBusy[i] : 0.0;
                        BitConverter.GetBytes(gb).CopyTo(buf, i * (int)blobSize + (int)gpuBusyOffset);
                        if (presentModes is not null)
                            BitConverter.GetBytes(presentModes[i]).CopyTo(buf, i * (int)blobSize + 24);
                        if (displayLatencies is not null)
                            BitConverter.GetBytes(displayLatencies[i]).CopyTo(buf, i * (int)blobSize + (int)displayLatencyOffset);
                    }
                    c[4] = (uint)frametimes.Length;
                }
                else
                {
                    c[4] = 0u;
                }
                return PM_STATUS.PM_STATUS_SUCCESS;
            });
        api.FreeFrameQuery(Query).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.StopTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.CloseSession(Session).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        return api;
    }

    private static PresentMonSession Sut(IPresentMonApi api) => new(api, new FakeClock());

    [Fact]
    public async Task Capture_parses_frametimes_from_consumed_blobs()
    {
        var api = ApiYielding(new[] { 8.3, 8.4 });
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().BeEquivalentTo(new[] { 8.3, 8.4 });
    }

    [Fact]
    public async Task Capture_reads_frametime_at_the_registered_offset_not_zero()
    {
        // frametime lives at offset 8 this time — proves we honor the registered dataOffset.
        var api = ApiYielding(new[] { 12.5, 13.0 }, blobSize: 16, frametimeOffset: 8, latencyOffset: 0);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().BeEquivalentTo(new[] { 12.5, 13.0 });
    }

    [Fact]
    public async Task Capture_decodes_render_present_latency_at_the_registered_offset()
    {
        // The query's 2nd element (RENDER_PRESENT_LATENCY) lands at offset 8; the service writes
        // it there per frame. We decode it as a parallel per-frame series alongside the frametimes.
        var api = ApiYielding(new[] { 8.3, 8.4 }, latencies: new[] { 3.1, 3.5 });
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().BeEquivalentTo(new[] { 8.3, 8.4 });
        r.Value.RenderPresentLatencyMs.Should().BeEquivalentTo(new[] { 3.1, 3.5 });
    }

    // B4 (10/10/2026) : la latence d'affichage, lue à son décalage et à la taille annoncée ; non annoncée = null, jamais zéro.
    [Fact]
    public async Task Capture_decodes_display_latency_at_the_registered_offset_and_null_when_not_delivered()
    {
        var avec = await new PresentMonSession(ApiYielding(new[] { 8.0, 8.0 }, blobSize: 48, displayLatencies: new[] { 21.5, 23.5 }), new FakeClock())
            .CaptureAsync(42, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        avec.IsOk.Should().BeTrue(avec.Error?.Message);
        avec.Value!.DisplayLatencyMs.Should().Equal(21.5, 23.5);

        var sans = await new PresentMonSession(ApiYielding(new[] { 8.0, 8.0 }, blobSize: 48), new FakeClock())
            .CaptureAsync(42, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        sans.IsOk.Should().BeTrue(sans.Error?.Message);
        sans.Value!.DisplayLatencyMs.Should().BeNull("série non annoncée par le service");
    }

    [Fact]
    public async Task Capture_decodes_gpu_busy_at_the_registered_offset()
    {
        // The query's 3rd element (GPU_BUSY) lands at offset 16; decode it as a parallel series.
        var api = ApiYielding(new[] { 8.3, 8.4 }, gpuBusy: new[] { 7.0, 7.5 });
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.GpuBusyMs.Should().BeEquivalentTo(new[] { 7.0, 7.5 });
    }

    [Fact]
    public async Task Capture_decodes_present_mode_at_the_size_the_service_announces()
    {
        // 3 = independent flip, 4 = composed flip (PM_PRESENT_MODE, PresentMon 2.5.1).
        var api = ApiYielding(new[] { 8.3, 8.4, 8.5 }, blobSize: 28, presentModes: new[] { 3, 4, 3 }, presentModeSize: 4);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.PresentModes.Should().Equal(3, 4, 3);
        r.Value.PartComposee.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public async Task Capture_leaves_present_mode_empty_when_its_size_is_not_announced()
    {
        var api = ApiYielding(new[] { 8.3, 8.4 }, blobSize: 28, presentModes: new[] { 4, 4 }, presentModeSize: 0);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.PresentModes.Should().BeEmpty();
        r.Value.PartComposee.Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { 0, 0 }, null)]
    [InlineData(new[] { 0, 4, 8 }, 0.5)]
    [InlineData(new[] { 5, 6, 4, 1 }, 0.75)]
    public void PartComposee_ignores_unknown_frames(int[] modes, double? attendu)
    {
        var c = new FrameCapture(new[] { 1.0 }, Array.Empty<double>(), Array.Empty<double>(), PresentModes: modes);
        c.PartComposee.Should().Be(attendu);
    }

    // Mesure du 06/10/2026 (scène D3D11, service Intel 2.5.1) : le service n'active ses fournisseurs
    // ETW qu'au pmStartTrackingProcess, et la première image arrive 1 à 2 s plus tard. Une capture
    // d'1 s rendait « aucune image » ; une de 3 s n'en couvrait qu'une seconde.
    [Fact]
    public async Task Capture_waits_out_the_service_warmup_and_then_measures_the_full_duration()
    {
        var api = ApiYielding(Array.Empty<double>());
        var polls = 0;
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                // 40 polls vides (2 s à 50 ms par lecture d'horloge), puis une image par poll.
                if (++polls <= 40) { c[4] = 0u; return PM_STATUS.PM_STATUS_SUCCESS; }
                BitConverter.GetBytes(5.0).CopyTo((byte[])c[2], 0);
                c[4] = 1u;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });

        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromSeconds(1), default);

        r.IsOk.Should().BeTrue("le service a mis 2 s à démarrer, la capture d'1 s doit l'attendre");
        // 1 s mesurée À PARTIR de la première image : 20 polls de 50 ms, plus celui qui l'a apportée.
        r.Value!.FrameTimesMs.Should().HaveCount(21);
    }

    [Fact]
    public async Task Capture_invalid_pid_fails_pid_invalid()
    {
        var api = Substitute.For<IPresentMonApi>();
        var r = await Sut(api).CaptureAsync(0, TimeSpan.FromMilliseconds(10), default);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PidInvalid);
    }

    [Fact]
    public async Task Capture_zero_frames_fails_no_frames()
    {
        var api = ApiYielding(Array.Empty<double>());
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);
        r.Error!.Value.Code.Should().Be(ErrorCodes.NoFrames);
    }

    [Fact]
    public async Task Capture_start_tracking_invalid_pid_maps_to_pid_invalid()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_INVALID_PID);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PidInvalid);
    }

    [Fact]
    public async Task Capture_open_session_failure_degrades_pmon_unavailable()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SERVICE_ERROR);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PmonUnavailable);
    }

    [Fact]
    public async Task Capture_register_query_failure_maps_to_capture_fail()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.RegisterFrameQuery(Session, Arg.Any<PM_QUERY_ELEMENT[]>(), out Arg.Any<nint>(), out Arg.Any<uint>())
            .Returns(PM_STATUS.PM_STATUS_FAILURE);
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);
        r.Error!.Value.Code.Should().Be(ErrorCodes.CaptureFail);
    }

    [Fact]
    public async Task Capture_always_closes_session_even_when_consume_throws()
    {
        var api = ApiYielding(new[] { 8.3 });
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(_ => throw new InvalidOperationException("boom")); // simulate a native/marshalling fault
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.CaptureFail);
        api.Received(1).CloseSession(Session);
        api.Received(1).StopTracking(Session, Pid);
        api.Received(1).FreeFrameQuery(Query);
    }

    [Fact]
    public async Task Capture_does_not_close_session_when_open_failed()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SERVICE_ERROR);
        await Sut(api).CaptureAsync(42, TimeSpan.FromMilliseconds(10), default);
        api.DidNotReceive().CloseSession(Arg.Any<nint>());
    }

    [Fact]
    public async Task Capture_honors_cancellation_token()
    {
        var api = ApiYielding(new[] { 8.3 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Sut(api).CaptureAsync(42, TimeSpan.FromSeconds(5), cts.Token);
        r.IsOk.Should().BeFalse();
    }

    [Fact]
    public void IsServiceAvailable_false_when_open_session_errs_no_throw()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SERVICE_ERROR);
        var r = Sut(api).IsServiceAvailable();
        r.IsOk.Should().BeTrue();
        r.Value.Should().BeFalse();
    }

    [Fact]
    public void IsServiceAvailable_true_closes_the_probe_session()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = (nint)9; return PM_STATUS.PM_STATUS_SUCCESS; });
        Sut(api).IsServiceAvailable().Value.Should().BeTrue();
        api.Received(1).CloseSession((nint)9);
    }

    [Fact]
    public void IsServiceAvailable_swallows_dll_not_found()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(_ => throw new DllNotFoundException("no dll"));
        var r = Sut(api).IsServiceAvailable();
        r.IsOk.Should().BeTrue();
        r.Value.Should().BeFalse();
    }
    // 07/10/2026 (J3) : le service PresentMon est partagé ; un autre client qui suit déjà le jeu laisse un arriéré que
    // notre premier pmConsumeFrames rend tout de suite (mesuré le 06/10 : 9,9 s d'images pour 6 s de capture). Les images
    // démarrées avant le début du suivi (instant QPC de chaque image) ne comptent pas.
    [Fact]
    public async Task Les_images_d_avant_le_suivi_laissees_par_un_autre_client_ne_comptent_pas()
    {
        const uint Blob = 40;
        const int OffsetQpc = 32;
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.RegisterFrameQuery(Session, Arg.Any<PM_QUERY_ELEMENT[]>(), out Arg.Any<nint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var elements = (PM_QUERY_ELEMENT[])c[1];
                elements[0].dataOffset = 0;
                elements[8].dataOffset = OffsetQpc;
                elements[8].dataSize = 8;
                c[2] = Query;
                c[3] = Blob;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });
        var (temps, qpcs) = (new[] { 99.0, 98.0, 8.3, 8.4 }, new ulong[] { 50, 60, 150, 160 });
        var premier = true;
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var buf = (byte[])c[2];
                if (!premier) { c[4] = 0u; return PM_STATUS.PM_STATUS_SUCCESS; }
                premier = false;
                for (var i = 0; i < temps.Length; i++)
                {
                    BitConverter.GetBytes(temps[i]).CopyTo(buf, i * (int)Blob);
                    BitConverter.GetBytes(qpcs[i]).CopyTo(buf, i * (int)Blob + OffsetQpc);
                }
                c[4] = (uint)temps.Length;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });

        var r = await new PresentMonSession(api, new FakeClock(), qpcMaintenant: () => 100)
            .CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().Equal(8.3, 8.4);
    }

    // 07/10/2026 (J3) : le service livre les images avec 1 à 2 s de retard ; mesurée sur l'horloge de DrDXT, une capture de
    // 6 s n'en gardait que 4,2. Sur les instants des images : la fenêtre va de la première image gardée à + durée, et la
    // première image au-delà la ferme. Ici 1 000 unités QPC par seconde, 30 ms = 30 unités : 100, 110, 120 comptent, 135 non.
    [Fact]
    public async Task La_duree_se_mesure_sur_les_instants_des_images()
    {
        const uint Blob = 40;
        const int OffsetQpc = 32;
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.RegisterFrameQuery(Session, Arg.Any<PM_QUERY_ELEMENT[]>(), out Arg.Any<nint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var elements = (PM_QUERY_ELEMENT[])c[1];
                elements[8].dataOffset = OffsetQpc;
                elements[8].dataSize = 8;
                c[2] = Query;
                c[3] = Blob;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });
        var (temps, qpcs) = (new[] { 10.0, 10.1, 10.2, 10.3, 10.4 }, new ulong[] { 100, 110, 120, 135, 140 });
        var premier = true;
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var buf = (byte[])c[2];
                if (!premier) { c[4] = 0u; return PM_STATUS.PM_STATUS_SUCCESS; }
                premier = false;
                for (var i = 0; i < temps.Length; i++)
                {
                    BitConverter.GetBytes(temps[i]).CopyTo(buf, i * (int)Blob);
                    BitConverter.GetBytes(qpcs[i]).CopyTo(buf, i * (int)Blob + OffsetQpc);
                }
                c[4] = (uint)temps.Length;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });

        var r = await new PresentMonSession(api, new FakeClock(), qpcMaintenant: () => 50, qpcParSeconde: 1000)
            .CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.Value!.FrameTimesMs.Should().Equal(10.0, 10.1, 10.2);
    }
    // Revue F-34 : un arriéré seul (aucune image d'après le suivi) n'est pas « aucune image » muet : le message dit
    // combien d'images ont été écartées et pourquoi, pour qu'une horloge qui ne coïncide pas se voie.
    [Fact]
    public async Task Un_arriere_seul_se_dit_dans_l_echec()
    {
        const uint Blob = 40;
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = Session; return PM_STATUS.PM_STATUS_SUCCESS; });
        api.StartTracking(Session, Pid).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.RegisterFrameQuery(Session, Arg.Any<PM_QUERY_ELEMENT[]>(), out Arg.Any<nint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var elements = (PM_QUERY_ELEMENT[])c[1];
                elements[8].dataOffset = 32;
                elements[8].dataSize = 8;
                c[2] = Query;
                c[3] = Blob;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });
        var premier = true;
        api.ConsumeFrames(Query, Pid, Arg.Any<byte[]>(), Arg.Any<uint>(), out Arg.Any<uint>())
            .Returns(c =>
            {
                var buf = (byte[])c[2];
                if (!premier) { c[4] = 0u; return PM_STATUS.PM_STATUS_SUCCESS; }
                premier = false;
                for (var i = 0; i < 2; i++)
                {
                    BitConverter.GetBytes(16.6).CopyTo(buf, i * (int)Blob);
                    BitConverter.GetBytes((ulong)(10 + i)).CopyTo(buf, i * (int)Blob + 32);
                }
                c[4] = 2u;
                return PM_STATUS.PM_STATUS_SUCCESS;
            });

        var r = await new PresentMonSession(api, new FakeClock(), qpcMaintenant: () => 100)
            .CaptureAsync(42, TimeSpan.FromMilliseconds(30), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NoFrames);
        r.Error!.Value.Message.Should().Contain("2 image(s) écartée(s)");
    }
}
