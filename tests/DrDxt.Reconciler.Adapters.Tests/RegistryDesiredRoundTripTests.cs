using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// Canonical round-trip pin: a known registry DWORD read back through the probe's
// serialization must equal the exact string the Application catalog would store as
// the manifest's DesiredValueJson — otherwise the reconciler's ordinal desired-vs-
// actual comparison (StringComparison.Ordinal) would see false drift.
//
// NvidiaUltraLowLatencyTweak writes Ultra = 2 via value.ToString(InvariantCulture) → "2".
// The probe must read a DWORD 2 back as exactly "2".
// -----------------------------------------------------------------------
public sealed class RegistryDesiredRoundTripTests
{
    // The desired value the Application's NvidiaUltraLowLatencyTweak applies (Ultra = 2),
    // serialized the same way the tweak serializes it for the manifest.
    private const uint UltraLowLatency = 2;
    private static readonly string DesiredJson =
        UltraLowLatency.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // A fake raw registry reader that hands the probe a DWORD already canonicalized by
    // the same canonicalizer the real Win32RegistryValueReader uses — proving the
    // dispatch surfaces that exact string unchanged.
    private sealed class CanonicalizingFakeRegistry : IRawRegistryValueReader
    {
        private readonly uint _dword;
        public CanonicalizingFakeRegistry(uint dword) => _dword = dword;
        public Result<string> ReadCanonical(RegistryDescriptor descriptor)
            => Result<string>.Ok(ReconcilableValueCanonicalizer.Dword(_dword));
    }

    [Fact]
    public async Task ReflexDword_RoundTripsToTheExactDesiredJson()
    {
        var probe = new SurfaceSettingProbe(
            new DefaultReconcilableTweakCatalog(),
            new CanonicalizingFakeRegistry(UltraLowLatency),
            new ThrowingPower(),
            new ThrowingService());

        var r = await probe.ReadCurrentValueAsync("latency.reflex.lowLatencyMode", default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(DesiredJson);   // exact byte-comparable equality
        r.Value.Should().Be("2");
    }

    [Fact]
    public void Canonicalizer_IsIdenticalToTheTweaksSerialization()
        => ReconcilableValueCanonicalizer.Dword(UltraLowLatency).Should().Be(DesiredJson);

    private sealed class ThrowingPower : IRawPowerPlanReader
    {
        public Result<string> ReadActiveSchemeCanonical()
            => Result<string>.Fail(ErrorCodes.PowerFail, "unused");
    }

    private sealed class ThrowingService : IRawServiceStateReader
    {
        public Result<string> ReadStateCanonical(string serviceName)
            => Result<string>.Fail(ErrorCodes.IoFailure, "unused");
    }
}
