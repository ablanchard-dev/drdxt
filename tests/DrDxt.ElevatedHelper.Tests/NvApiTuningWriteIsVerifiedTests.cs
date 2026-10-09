using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Observed on a real RTX 4060 on 01/09/2026: asking for a 110% power limit printed
/// "power limit applied: 110%" while an independent read two lines later returned 100%. The
/// card has no power headroom, the driver clamped in silence, and TrySetPowerLimitPercent
/// returned true — because it reports that the CALL was accepted, not that the VALUE landed.
///
/// A write that reports a success it did not obtain is worse than a write that fails: the
/// product would tell a customer "we raised your power limit" while nothing moved, and the
/// revert snapshot would record a value the GPU never held.
///
/// The rule these pin: An action reads back before it affirms.
/// </summary>
public sealed class NvApiTuningWriteIsVerifiedTests
{
    [Fact]
    public void A_power_limit_the_driver_silently_clamped_is_not_reported_as_applied()
    {
        // The exact shape observed: asked 110, driver holds 100, native says "accepted".
        var native = new StubNative { PowerLimitPct = 100, SetPowerSucceeds = true };

        var r = new NvApiTuningActions(native).SetPowerLimitPercent(0, 110f);

        r.IsOk.Should().BeFalse("le driver a garde 100 %, l'ecriture n'a pas eu lieu");
        r.Error!.Value.Message.Should().Contain("110").And.Contain("100");
    }

    [Fact]
    public void A_power_limit_that_landed_is_reported_as_applied()
    {
        var native = new StubNative { PowerLimitPct = 95, SetPowerSucceeds = true, EchoWrites = true };

        new NvApiTuningActions(native).SetPowerLimitPercent(0, 95f).IsOk.Should().BeTrue();
    }

    [Fact]
    public void A_core_offset_the_driver_did_not_take_is_not_reported_as_applied()
    {
        var native = new StubNative { CoreOffsetMhz = 0, SetCoreSucceeds = true };

        var r = new NvApiTuningActions(native).SetCoreClockOffset(0, 100);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("100").And.Contain("0");
    }

    [Fact]
    public void A_core_offset_that_landed_is_reported_as_applied()
    {
        var native = new StubNative { SetCoreSucceeds = true, EchoWrites = true };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeTrue();
    }

    [Fact]
    public void A_write_that_cannot_be_read_back_is_not_claimed_as_a_success()
    {
        // "Applied but unconfirmed" is not a success. Saying so is the whole point.
        var native = new StubNative { SetCoreSucceeds = true, EchoWrites = true, ReadCoreSucceeds = false };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeFalse();
    }

    [Fact]
    public void A_write_the_native_boundary_refuses_still_fails_as_before()
    {
        var native = new StubNative { SetCoreSucceeds = false };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeFalse();
    }

    private sealed class StubNative : INvApiNative
    {
        public bool IsAvailable => true;
        public bool EchoWrites { get; set; }          // le driver prend la valeur telle quelle
        public bool ReadCoreSucceeds { get; set; } = true;
        public bool ReadPowerSucceeds { get; set; } = true;
        public bool SetCoreSucceeds { get; set; }
        public bool SetPowerSucceeds { get; set; }
        public int CoreOffsetMhz { get; set; }
        public int PowerLimitPct { get; set; }

        public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
        { offsetMhz = CoreOffsetMhz; return ReadCoreSucceeds; }

        public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
        { offsetMhz = 0; return true; }

        public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
        { powerLimitPct = PowerLimitPct; return ReadPowerSucceeds; }

        public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz)
        { if (SetCoreSucceeds && EchoWrites) CoreOffsetMhz = offsetMhz; return SetCoreSucceeds; }

        public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz) => false;

        public bool TrySetPowerLimitPercent(int gpuIndex, float pct)
        { if (SetPowerSucceeds && EchoWrites) PowerLimitPct = (int)MathF.Round(pct); return SetPowerSucceeds; }

        public bool TrySetDrsGlobalSetting(uint settingId, uint value) => false;
        public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value) => false;
        public bool TryGetDrsGlobalSetting(uint settingId, out uint value) { value = 0; return false; }
        public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading) { reading = default; return false; }
        public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        { reading = default; return false; }
    }
}
