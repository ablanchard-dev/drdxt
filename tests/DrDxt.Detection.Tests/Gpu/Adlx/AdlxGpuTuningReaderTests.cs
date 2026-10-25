using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningReaderTests : tests unitaires sur faux d'AdlxGpuTuningReader.
//
// Tous les tests utilisent FakeAdlxNative : aucun appel ADLX natif, aucune carte AMD requise.
// La suite fige :
//   1. Échec propre : natif indisponible → chaque lecture rend Fail.
//   2. Transmission : natif réussi → le lecteur traduit les lectures brutes en Result.Ok.
//   3. Codes d'erreur : chaque échec porte ErrorCodes.TweakSdkUnavailable.
//   4. Garde d'entrée : index GPU négatif → Fail avant tout appel natif.
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningReaderTests
{
    // ── IsAvailable ──────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_is_false_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_is_true_when_native_available()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        sut.IsAvailable.Should().BeTrue();
    }

    // ── GetUndervoltValue ─────────────────────────────────────────────────

    [Fact]
    public void GetUndervoltValue_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetUndervoltValue(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetUndervoltValue_returns_ok_with_volt_mv_when_gfx_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            GfxSuccess  = true,
            GfxReads    = new AdlxGfxReads { VoltMv = 950 },
        };
        var r = new AdlxGpuTuningReader(fake).GetUndervoltValue(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(950);
    }

    [Fact]
    public void GetUndervoltValue_returns_fail_when_gfx_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetUndervoltValue(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetUndervoltValue_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetUndervoltValue(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── ResolveTunableGpuIndex (choix de la carte dédiée) ────────────────

    [Fact]
    public void ResolveTunableGpuIndex_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.ResolveTunableGpuIndex();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ResolveTunableGpuIndex_returns_zero_when_first_index_is_tunable()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = true };
        var r    = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(0);
    }

    [Fact]
    public void ResolveTunableGpuIndex_skips_unsupported_igpu_and_picks_discrete_card()
    {
        // Machine à 2 cartes : l'iGPU à l'index 0 n'est pas réglable (lecture en échec) ; la carte AMD
        // dédiée à l'index 1 se lit → le résolveur doit choisir l'index 1, pas l'iGPU en 0.
        var fake = new FakeAdlxNative
        {
            IsAvailable        = true,
            GfxSuccessForIndex = i => i == 1,
        };
        var r = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(1);
    }

    [Fact]
    public void ResolveTunableGpuIndex_fails_clean_when_no_index_reads()
    {
        // Machine sans GPU réglable : aucun index ne lit son réglage GFX → échec propre, jamais un index.
        var fake = new FakeAdlxNative { IsAvailable = true, GfxSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).ResolveTunableGpuIndex();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void ResolveTunableGpuIndex_never_throws_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var act = () => sut.ResolveTunableGpuIndex();
        act.Should().NotThrow();
    }

    // ── GetPowerLimitRange : la plage dit l'unité de la limite (décalage ou % absolu). ──

    [Fact]
    public void GetPowerLimitRange_rend_la_plage_lue_et_echoue_proprement_sans_lecture()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable  = true,
            PowerSuccess = true,
            PowerReads   = new AdlxPowerReads { PowerLimitPct = 0, PowerRange = new AdlxIntRange { Min = -10, Max = 15, Step = 1 } },
        };
        new AdlxGpuTuningReader(fake).GetPowerLimitRange(0).Value.Should().Be(new DrDxt.Application.Tweaks.Hardware.PlageDePuissanceAmd(-10, 15, 1));

        new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true, PowerSuccess = false }).GetPowerLimitRange(0)
            .Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        new AdlxGpuTuningReader(fake).GetPowerLimitRange(-1).IsOk.Should().BeFalse();
    }

    // ── GetPowerLimitPct ──────────────────────────────────────────────────

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_ok_with_pct_when_power_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable  = true,
            PowerSuccess = true,
            PowerReads   = new AdlxPowerReads { PowerLimitPct = 90 },
        };
        var r = new AdlxGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(90);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_power_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, PowerSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetPowerLimitPct(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetFanCurve ───────────────────────────────────────────────────────

    [Fact]
    public void GetFanCurve_returns_fail_when_native_unavailable()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_ok_with_mapped_points_when_fan_read_succeeds()
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            FanSuccess  = true,
            FanReads    = new AdlxFanReads
            {
                Points = new[]
                {
                    new AdlxFanPoint { TempC = 60, SpeedPct = 50 },
                    new AdlxFanPoint { TempC = 80, SpeedPct = 75 },
                },
            },
        };
        var r = new AdlxGpuTuningReader(fake).GetFanCurve(0);
        r.IsOk.Should().BeTrue();
        r.Value!.Points.Should().HaveCount(2);
        r.Value.Points[0].Should().Be(new FanCurvePoint(60, 50));
        r.Value.Points[1].Should().Be(new FanCurvePoint(80, 75));
    }

    // Une lecture réussie sans point n'est pas une courbe : le levier du ventilateur bâtirait son retour d'une courbe vide.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetFanCurve_sans_point_n_est_pas_une_courbe(bool pointsNuls)
    {
        var fake = new FakeAdlxNative
        {
            IsAvailable = true,
            FanSuccess  = true,
            FanReads    = new AdlxFanReads { Points = pointsNuls ? null : [] },
        };

        new AdlxGpuTuningReader(fake).GetFanCurve(0).IsOk.Should().BeFalse();
    }

    [Fact]
    public void GetFanCurve_returns_fail_when_fan_read_fails()
    {
        var fake = new FakeAdlxNative { IsAvailable = true, FanSuccess = false };
        var r    = new AdlxGpuTuningReader(fake).GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_fail_for_negative_gpu_index()
    {
        var sut = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = true });
        var r   = sut.GetFanCurve(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── Natif indisponible : ne lève jamais ──────────────────────────────────

    [Fact]
    public void All_reads_on_unavailable_native_never_throw()
    {
        var sut  = new AdlxGpuTuningReader(new FakeAdlxNative { IsAvailable = false });
        var act1 = () => sut.GetUndervoltValue(0);
        var act2 = () => sut.GetPowerLimitPct(0);
        var act3 = () => sut.GetFanCurve(0);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
    }
}

// ────────────────────────────────────────────────────────────────────────
// FakeAdlxNative : substitut en processus pour les tests unitaires.
// N'appelle jamais amdadlx64.dll ; entièrement configurable par test.
//
// Garantie d'écriture : les propriétés Set* et les champs LastSet* sont la seule
// façon d'observer les appels d'écriture. Les vrais P/Invoke SetGPUMaxFrequency / SetPowerLimit
// ne sont jamais atteints : amdadlx64.dll n'est pas chargée dans les processus de test.
// ────────────────────────────────────────────────────────────────────────
internal sealed class FakeAdlxNative : IAdlxNative
{
    public bool IsAvailable { get; set; }

    // ── état de lecture ───────────────────────────────────────────────────

    // Réglage graphique
    public bool         GfxSuccess { get; set; } = true;
    public AdlxGfxReads GfxReads   { get; set; }

    /// <summary>Dérogation optionnelle par index du succès de lecture GFX : modélise une machine
    /// iGPU + dGPU où seul l'index de la carte dédiée se lit. Null → repli sur
    /// <see cref="GfxSuccess"/> pour chaque index.</summary>
    public Func<int, bool>? GfxSuccessForIndex { get; set; }

    // Réglage de puissance
    public bool           PowerSuccess { get; set; } = true;
    public AdlxPowerReads PowerReads   { get; set; }

    // Réglage de ventilation
    public bool         FanSuccess { get; set; } = true;
    public AdlxFanReads FanReads   { get; set; }

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        reads = GfxReads;
        return GfxSuccessForIndex?.Invoke(gpuIndex) ?? GfxSuccess;
    }

    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads)
    {
        reads = PowerReads;
        return PowerSuccess;
    }

    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads)
    {
        reads = FanReads;
        return FanSuccess;
    }

    // ── état d'écriture ───────────────────────────────────────────────────────
    // Ces champs enregistrent et contrôlent le comportement d'écriture sans toucher aucun pilote.

    /// <summary>Contrôle si TrySetGfx rend true (défaut) ou false.</summary>
    public bool SetGfxSuccess { get; set; } = true;

    /// <summary>Ce que TrySetGfx rend dans 'original' ; par défaut GfxReads, pour que les tests
    /// configurent une seule source de l'état GFX courant.</summary>
    public AdlxGfxReads SetGfxOriginal { get; set; }

    /// <summary>Le couple (MaxFreqMhz, VoltMv) passé en dernier à TrySetGfx, ou null si
    /// TrySetGfx n'a jamais été appelé. Sert à vérifier ce qui a été écrit.</summary>
    public (int MaxFreqMhz, int VoltMv)? LastSetGfx { get; private set; }

    /// <summary>Nombre total d'appels à TrySetGfx (pour vérifier que l'écriture n'a pas eu lieu).</summary>
    public int SetGfxCallCount { get; private set; }

    /// <summary>Contrôle si TrySetPowerLimit rend true (défaut) ou false.</summary>
    public bool SetPowerSuccess { get; set; } = true;

    /// <summary>Ce que TrySetPowerLimit rend dans 'original'.</summary>
    public AdlxPowerReads SetPowerOriginal { get; set; }

    /// <summary>Le pourcentage de limite de puissance passé en dernier à TrySetPowerLimit, ou null.</summary>
    public int? LastSetPower { get; private set; }

    /// <summary>Nombre total d'appels à TrySetPowerLimit.</summary>
    public int SetPowerCallCount { get; private set; }

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        original = SetGfxOriginal;
        SetGfxCallCount++;
        if (!IsAvailable || !SetGfxSuccess) return false;
        LastSetGfx = (newMaxFreqMhz, newVoltMv);
        return true;
    }

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        original = SetPowerOriginal;
        SetPowerCallCount++;
        if (!IsAvailable || !SetPowerSuccess) return false;
        LastSetPower = newPowerLimitPct;
        return true;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────
    // Synthétise une sonde depuis les drapeaux du faux pour que les tests exercent la
    // surface de diagnostic sans amdadlx64.dll.
    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex)
    {
        if (!IsAvailable)
            return new[] { new GpuProbeStep("adlx-init", false, "fake: ADLX unavailable") };
        if (!GfxSuccess)
            return new[]
            {
                new GpuProbeStep("adlx-init", true),
                new GpuProbeStep("read-gfx-v2", false, "fake: gfx read failed"),
            };
        return new[]
        {
            new GpuProbeStep("adlx-init", true),
            new GpuProbeStep("read-gfx-v2", true, "fake: ok"),
        };
    }
}
