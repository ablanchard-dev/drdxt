using DrDxt.Detection.Wmi;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// AddDrDxtReconcilerAdapters registers the 3 production probes + the descriptor
// catalog seam against their interfaces. The Host/App wire this later — here we only
// assert the container resolves each interface to the right concrete adapter.
// -----------------------------------------------------------------------
public sealed class AdaptersServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // WmiGpuDriverProbe needs IWmiQuery — supplied by Detection's registration.
        services.AddSingleton<IWmiQuery, ManagementWmiQuery>();
        services.AddDrDxtReconcilerAdapters();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegistersDescriptorCatalog()
        => BuildProvider().GetService<IReconcilableTweakCatalog>()
            .Should().BeOfType<DefaultReconcilableTweakCatalog>();

    [Fact]
    public void RegistersOsBuildProbe()
        => BuildProvider().GetService<IOsBuildProbe>()
            .Should().BeOfType<RegistryOsBuildProbe>();

    [Fact]
    public void RegistersGpuDriverProbe()
        => BuildProvider().GetService<IGpuDriverProbe>()
            .Should().BeOfType<WmiGpuDriverProbe>();

    [Fact]
    public void RegistersSettingProbe()
        => BuildProvider().GetService<ISettingProbe>()
            .Should().BeOfType<SurfaceSettingProbe>();

    [Fact]
    public void RegistersReapplyOpMapper_ResolvableFromTheCatalogAlone()
        => BuildProvider().GetService<IReapplyOpMapper>()
            .Should().BeOfType<DefaultReapplyOpMapper>();

    [Fact]
    public void RegistersElevatedReapplyClient_AsItsImplementation()
    {
        // ElevatedReapplyClient also needs IElevatedClient (a Host/App prerequisite, like
        // IWmiQuery), so we assert the REGISTRATION's implementation type rather than
        // forcing construction here.
        var services = new ServiceCollection();
        services.AddSingleton<IWmiQuery, ManagementWmiQuery>();
        services.AddDrDxtReconcilerAdapters();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IElevatedReapplyClient)
            && d.ImplementationType == typeof(ElevatedReapplyClient));
    }

    [Fact]
    public void ReturnsTheSameServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        services.AddDrDxtReconcilerAdapters().Should().BeSameAs(services);
    }
}
