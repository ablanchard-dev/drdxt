using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

public class NvApiLatencyReaderTests
{
    private readonly FakeNvApiNative _native = new();
    private readonly FakeReg _reg = new();

    private NvApiLatencyReader Reader() => new(_native, _reg);

    [Fact]
    public void IsAvailable_follows_the_native_boundary()
    {
        _native.IsAvailable = true;
        Reader().IsAvailable.Should().BeTrue();
        _native.IsAvailable = false;
        Reader().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void GetPreferredPstate_returns_the_drs_value_when_native_reads_it()
    {
        _native.DrsReadSuccess = true;
        _native.DrsSettingValue = 1; // PREFER_MAX
        var r = Reader().GetPreferredPstate();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(1u);
    }

    // Absent du profil de base (NVAPI_SETTING_NOT_FOUND), le réglage est au défaut du pilote : PREFERRED_PSTATE_DEFAULT =
    // OPTIMAL_POWER (5, NvApiDriverSettings.h). Lu « illisible », il faisait dire « état illisible » à tout pilote d'origine.
    // Une panne de lecture, elle, reste illisible.
    [Fact]
    public void Absent_du_profil_de_base_le_reglage_est_dit_au_defaut_et_la_lecture_reste_brute()
    {
        _native.DrsReadSuccess = false;
        _native.DrsAuDefaut = true;
        Reader().GetPreferredPstate().IsOk.Should().BeFalse("la lecture brute : l'inverse d'un levier ne se construit pas sur un défaut");
        Reader().PreferredPstateAuDefaut().Should().BeTrue();

        _native.DrsAuDefaut = false;
        Reader().PreferredPstateAuDefaut().Should().BeFalse("une panne n'est pas le défaut du pilote");
    }

    [Fact]
    public void Drs_getters_fail_clean_when_the_setting_is_not_readable()
    {
        _native.DrsReadSuccess = false; // NVAPI indisponible ou réglage par défaut
        Reader().GetVsyncMode().Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        Reader().GetFrameRateLimit().IsOk.Should().BeFalse();
        Reader().GetMaxPreRenderedFrames().IsOk.Should().BeFalse();
        Reader().GetPreferredPstate().IsOk.Should().BeFalse();
    }

    [Fact]
    public void GetLowLatencyMode_reads_the_nvtweak_registry_dword()
    {
        _reg.Value = 2; // Ultra
        var r = Reader().GetLowLatencyMode();
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(2u);
    }

    // Faux IRegistryReader minimal et réglable (le StubRegistryReader partagé échoue toujours).
    private sealed class FakeReg : IRegistryReader
    {
        public uint? Value { get; set; }
        public bool Fail { get; set; }
        // La valeur ne répond qu'à la clé NVTweak de NVIDIA : une lecture ailleurs rend « absente ».
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) =>
            Fail ? Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "x")
            : Result<uint?>.Ok(hive == RegistryHiveKind.LocalMachine && path == @"SOFTWARE\NVIDIA Corporation\Global\NVTweak"
                               && name == "LowLatencyMode" ? Value : null);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) =>
            Result<string?>.Ok(null);

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Ok(null);   // rien de multi-chaines dans ces scenarios
    }
}
