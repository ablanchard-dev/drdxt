using DrDxt.Detection.Wmi;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// AddDrDxtReconcilerAdapters enregistre les 3 sondes de production et la couture du catalogue
// de descripteurs sur leurs interfaces. L'hôte et l'App les câblent plus tard ; ici on vérifie
// seulement que le conteneur résout chaque interface vers le bon adaptateur concret.
// -----------------------------------------------------------------------
public sealed class AdaptersServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // WmiGpuDriverProbe a besoin d'IWmiQuery, fourni par l'enregistrement de Detection.
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
        // ElevatedReapplyClient a aussi besoin d'IElevatedClient (un prérequis hôte/App, comme
        // IWmiQuery), donc on vérifie le type d'implémentation de l'enregistrement plutôt que
        // de forcer la construction ici.
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
