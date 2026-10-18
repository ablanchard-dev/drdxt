using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiGpuTuningReaderTests : tests unitaires sur faux de NvApiGpuTuningReader.
//
// Tous les tests utilisent FakeNvApiNative : aucun appel NVAPI natif, aucune carte NVIDIA requise
// (nvapi64.dll n'est jamais chargée dans un processus de test). Miroir de la suite du lecteur ADLX :
//   1. Échec propre : natif indisponible → chaque lecture rend Fail (ne lève jamais).
//   2. Transmission : natif réussi → le lecteur traduit la lecture brute en Result.Ok.
//   3. Vraie lecture mémoire : les décalages cœur et mémoire sont deux vraies lectures distinctes ;
//      la lecture mémoire n'est pas un faux 0 (ApplyNvidiaAsync fait échouer l'undervolt s'il ne
//      peut pas lire le décalage mémoire, donc un placebo ici tuerait l'undervolt NVIDIA).
//   4. Codes d'erreur : chaque échec porte ErrorCodes.TweakSdkUnavailable.
//   5. Garde d'entrée : index GPU négatif → Fail avant tout appel natif.
//   6. Ventilation = indisponible, honnêtement : GetFanCurve échoue toujours proprement ; la lecture
//      de courbe NVAPI n'est pas dans la recette vérifiée ; jamais une courbe fausse ou vide.
// -----------------------------------------------------------------------
public sealed class NvApiGpuTuningReaderTests
{
    // ── IsAvailable ──────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_is_false_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_is_true_when_native_available()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        sut.IsAvailable.Should().BeTrue();
    }

    // ── GetCoreClockOffsetMhz ─────────────────────────────────────────────

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_ok_with_value_when_read_succeeds()
    {
        var fake = new FakeNvApiNative
        {
            IsAvailable   = true,
            CoreSuccess   = true,
            CoreOffsetMhz = -15,
        };
        var r = new NvApiGpuTuningReader(fake).GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(-15);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_when_read_fails()
    {
        // Le faux porte une valeur non nulle mais la lecture échoue → le lecteur doit rendre Fail,
        // jamais laisser fuir la valeur périmée comme un faux nombre.
        var fake = new FakeNvApiNative { IsAvailable = true, CoreSuccess = false, CoreOffsetMhz = 123 };
        var r    = new NvApiGpuTuningReader(fake).GetCoreClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetCoreClockOffsetMhz_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetCoreClockOffsetMhz(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetMemoryClockOffsetMhz (vraie lecture, pas d'échec propre par construction) ────

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_ok_with_REAL_value_when_read_succeeds()
    {
        // Le décalage mémoire est une vraie lecture sur la frontière native, pas un 0 codé en dur.
        var fake = new FakeNvApiNative
        {
            IsAvailable     = true,
            MemorySuccess   = true,
            MemoryOffsetMhz = 200,
        };
        var r = new NvApiGpuTuningReader(fake).GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(200);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_when_read_fails()
    {
        // Domaine d'horloge mémoire vraiment absent sur cette carte → le natif rend false →
        // le lecteur échoue proprement et honnêtement (jamais un 0 fabriqué).
        var fake = new FakeNvApiNative { IsAvailable = true, MemorySuccess = false, MemoryOffsetMhz = 999 };
        var r    = new NvApiGpuTuningReader(fake).GetMemoryClockOffsetMhz(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetMemoryClockOffsetMhz_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetMemoryClockOffsetMhz(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void Core_and_memory_offsets_are_independent_real_reads()
    {
        // Fige la correction de conception : cœur et mémoire sont deux vraies lectures séparées,
        // chacune traduisant sa propre valeur native ; la mémoire n'est pas un alias du cœur ni de 0.
        var fake = new FakeNvApiNative
        {
            IsAvailable     = true,
            CoreOffsetMhz   = -30,
            MemoryOffsetMhz = 500,
        };
        var sut = new NvApiGpuTuningReader(fake);
        sut.GetCoreClockOffsetMhz(0).Value.Should().Be(-30);
        sut.GetMemoryClockOffsetMhz(0).Value.Should().Be(500);
    }

    // ── GetPowerLimitPct ──────────────────────────────────────────────────

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_ok_with_pct_when_read_succeeds()
    {
        var fake = new FakeNvApiNative
        {
            IsAvailable   = true,
            PowerSuccess  = true,
            PowerLimitPct = 90,
        };
        var r = new NvApiGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(90);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_when_read_fails()
    {
        var fake = new FakeNvApiNative { IsAvailable = true, PowerSuccess = false, PowerLimitPct = 77 };
        var r    = new NvApiGpuTuningReader(fake).GetPowerLimitPct(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetPowerLimitPct_returns_fail_for_negative_gpu_index()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetPowerLimitPct(-1);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── GetFanCurve : indisponible, honnêtement, jamais une fausse courbe ──────

    [Fact]
    public void GetFanCurve_returns_honest_unavailable_even_when_native_available()
    {
        // La lecture de courbe NVAPI n'est pas dans la recette vérifiée et le levier de ventilation
        // est différé → échec propre honnête, jamais une courbe vide ou fabriquée.
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = true });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void GetFanCurve_returns_fail_when_native_unavailable()
    {
        var sut = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var r   = sut.GetFanCurve(0);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── Natif indisponible : ne lève jamais ──────────────────────────────────

    [Fact]
    public void All_reads_on_unavailable_native_never_throw()
    {
        var sut  = new NvApiGpuTuningReader(new FakeNvApiNative { IsAvailable = false });
        var act1 = () => sut.GetCoreClockOffsetMhz(0);
        var act2 = () => sut.GetMemoryClockOffsetMhz(0);
        var act3 = () => sut.GetPowerLimitPct(0);
        var act4 = () => sut.GetFanCurve(0);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
        act4.Should().NotThrow();
    }
}

// ────────────────────────────────────────────────────────────────────────
// FakeNvApiNative : substitut en processus pour les tests unitaires.
// N'appelle jamais nvapi64.dll ; entièrement configurable par test. Les lectures Try* rendent
// la valeur configurée et un drapeau de succès par surface ; les écritures Try*Set ne sont jamais
// atteintes par la couture de lecture (et rendent false ici : aucun pilote touché en test).
// ────────────────────────────────────────────────────────────────────────
internal sealed class FakeNvApiNative : INvApiNative
{
    public bool IsAvailable { get; set; }

    // ── état de lecture ───────────────────────────────────────────────────
    public bool CoreSuccess   { get; set; } = true;
    public int  CoreOffsetMhz { get; set; }

    public bool MemorySuccess   { get; set; } = true;
    public int  MemoryOffsetMhz { get; set; }

    public bool PowerSuccess  { get; set; } = true;
    public int  PowerLimitPct { get; set; }

    public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = CoreOffsetMhz;
        return CoreSuccess;
    }

    public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = MemoryOffsetMhz;
        return MemorySuccess;
    }

    public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
    {
        powerLimitPct = PowerLimitPct;
        return PowerSuccess;
    }

    // ── surface d'écriture : jamais exercée par le lecteur non élevé ─────────
    public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz) => false;
    public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz) => false;
    public bool TrySetPowerLimitPercent(int gpuIndex, float pct) => false;
    public bool TrySetDrsGlobalSetting(uint settingId, uint value) => false;
    // Cette doublure sert le seam de lecture : aucune ecriture ne touche un driver dans les tests.
    public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value) => false;

    // ── DRS read (non-elevated) ────────────────────────────────────────────
    public bool DrsReadSuccess { get; set; }
    public bool DrsAuDefaut { get; set; }
    public bool DrsGlobalAuDefaut(uint settingId) => DrsAuDefaut;
    public uint DrsSettingValue { get; set; }
    public uint DrsSettingLocation { get; set; }
    public uint DrsIsCurrentPredefined { get; set; }
    public uint DrsIsPredefinedValid { get; set; }

    public bool TryGetDrsGlobalSetting(uint settingId, out uint value)
    {
        value = DrsSettingValue;
        return DrsReadSuccess;
    }

    public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading)
    {
        reading = new DrsReading(DrsSettingValue, DrsSettingLocation,
            DrsIsCurrentPredefined, DrsIsPredefinedValid);
        return DrsReadSuccess;
    }

    // ── DRS read, profil par application ──────────────────────────────────
    public bool AppDrsReadSuccess { get; set; }
    public uint AppDrsSettingValue { get; set; }
    public uint AppDrsSettingLocation { get; set; }
    public uint AppDrsIsCurrentPredefined { get; set; }
    public int AppDrsCalls { get; private set; }

    public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
    {
        AppDrsCalls++;
        reading = new DrsReading(AppDrsSettingValue, AppDrsSettingLocation, AppDrsIsCurrentPredefined, 0);
        return AppDrsReadSuccess;
    }

    /// <summary>Echec au jeu parce que l'executable n'a aucun profil (EXECUTABLE_NOT_FOUND).</summary>
    public bool AppDrsPasDeProfil { get; set; }

    public INvApiNative.LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading)
        => TryReadDrsSettingForApplication(applicationPath, settingId, out reading)
            ? INvApiNative.LectureAuJeu.Lu
            : AppDrsPasDeProfil ? INvApiNative.LectureAuJeu.PasDeProfil : INvApiNative.LectureAuJeu.Illisible;
}
