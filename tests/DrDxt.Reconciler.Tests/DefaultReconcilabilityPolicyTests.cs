using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// La table fermée de réconciliabilité de production : les surfaces OS connues sont réconciliables,
// les surfaces protégées sont refusées net, les leviers inconnus sont rejetés par prudence.
// -----------------------------------------------------------------------
public sealed class DefaultReconcilabilityPolicyTests
{
    // Référencé par l'interface pour que l'implémentation par défaut d'IsReconcilable() soit visible.
    private readonly IReconcilabilityPolicy _policy = new DefaultReconcilabilityPolicy();

    [Theory]
    [InlineData("fixit.sanePowerPlan", ReconcileSurface.PowerPlan)]
    [InlineData("fixit.x3dVCacheService", ReconcileSurface.Service)]
    [InlineData("latency.reflex.lowLatencyMode", ReconcileSurface.Registry)]
    [InlineData("os.hags", ReconcileSurface.Registry)]                 // HAGS (HKLM DWORD)
    public void KnownTweaks_MapToTheirSurface_AndAreReconcilable(string id, ReconcileSurface surface)
    {
        _policy.SurfaceFor(id).Should().Be(surface);
        _policy.IsReconcilable(id).Should().BeTrue();
    }

    // Le magasin signe ce que la politique dit réconciliable, et la passe sonde par le catalogue : un levier connu de l'une
    // et pas de l'autre entrerait au manifeste, puis échouerait à chaque passe (RECONCILE_PARTIAL tous les jours).
    [Theory]
    [InlineData("fixit.sanePowerPlan")]
    [InlineData("fixit.x3dVCacheService")]
    [InlineData("latency.reflex.lowLatencyMode")]
    [InlineData("os.hags")]
    [InlineData("latency.renderQueueCap")]
    [InlineData("latency.vrrVsyncCap")]
    public void La_politique_et_le_catalogue_disent_la_meme_chose(string id)
        => _policy.IsReconcilable(id).Should().Be(new DefaultReconcilableTweakCatalog().DescriptorFor(id) is not null);

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
