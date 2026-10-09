using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// The production closed reconcilability table: known OS surfaces are reconcilable,
// protected surfaces are hard-refused, unknown tweaks are conservatively rejected.
// -----------------------------------------------------------------------
public sealed class DefaultReconcilabilityPolicyTests
{
    // Referenced through the interface so the default IsReconcilable() DIM is in scope.
    private readonly IReconcilabilityPolicy _policy = new DefaultReconcilabilityPolicy();

    [Theory]
    [InlineData("fixit.sanePowerPlan", ReconcileSurface.PowerPlan)]
    [InlineData("fixit.x3dVCacheService", ReconcileSurface.Service)]
    [InlineData("latency.reflex.lowLatencyMode", ReconcileSurface.Registry)]
    [InlineData("os.hags", ReconcileSurface.Registry)]                 // NS-1 HAGS (HKLM DWORD)
    public void KnownTweaks_MapToTheirSurface_AndAreReconcilable(string id, ReconcileSurface surface)
    {
        _policy.SurfaceFor(id).Should().Be(surface);
        _policy.IsReconcilable(id).Should().BeTrue();
    }

    [Fact]
    public void UnknownTweak_IsNotReconcilable()
    {
        _policy.SurfaceFor("totally.unknown").Should().BeNull();
        _policy.IsReconcilable("totally.unknown").Should().BeFalse();
    }

    [Theory]
    [InlineData("service.WinDefend")]
    [InlineData("service.wuauserv")]
    [InlineData("service.BITS")]
    public void ProtectedSurfaces_AreNeverReconcilable(string id)
    {
        _policy.IsProtected(id).Should().BeTrue();
        _policy.IsReconcilable(id).Should().BeFalse();
    }

    [Fact]
    public void NvidiaTweaks_CarryTheirVendor_ForDriverDriftSignal()
    {
        _policy.VendorFor("latency.reflex.lowLatencyMode").Should().Be("NVIDIA");
        _policy.VendorFor("fixit.sanePowerPlan").Should().BeNull();
    }
}
