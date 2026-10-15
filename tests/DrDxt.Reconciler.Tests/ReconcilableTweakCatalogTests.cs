using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// La table de descripteurs réconciliables (seule source) et le sérialiseur de valeur
// canonique. Données pures : la SettingProbe et le mappeur d'ops la consomment tous deux,
// donc elle vit (et se teste) dans la couche pure DrDxt.Reconciler.
// -----------------------------------------------------------------------
public sealed class ReconcilableTweakCatalogTests
{
    private readonly DefaultReconcilableTweakCatalog _catalog = new();

    [Fact]
    public void Reflex_HasReadableRegistryLeaf_MatchingTheApplicationTweak()
    {
        var d = _catalog.DescriptorFor("latency.reflex.lowLatencyMode");

        d.Should().NotBeNull();
        d!.Surface.Should().Be(ReconcileSurface.Registry);
        d.Registry.Should().NotBeNull();
        d.Registry!.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        d.Registry.Path.Should().Be(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak");
        d.Registry.Name.Should().Be("LowLatencyMode");
        d.Registry.Kind.Should().Be(RegistryValueType.DWord);
    }

    [Fact]
    public void PowerPlan_IsPowerPlanSurface_WithNoConcreteSubLocation()
    {
        var d = _catalog.DescriptorFor("fixit.sanePowerPlan");

        d.Should().NotBeNull();
        d!.Surface.Should().Be(ReconcileSurface.PowerPlan);
        d.Registry.Should().BeNull();
        d.ServiceName.Should().BeNull();
    }

    [Fact]
    public void X3dService_IsServiceSurface_WithTheAmdServiceName()
    {
        var d = _catalog.DescriptorFor("fixit.x3dVCacheService");

        d.Should().NotBeNull();
        d!.Surface.Should().Be(ReconcileSurface.Service);
        d.ServiceName.Should().Be("AMD3DVCacheSvc");
    }

    [Fact]
    public void DrsBackedLatencyIds_AreNotProbeable_UntilTheOpMapperResolvesTheirReadPath()
    {
        // La politique les classe Registry, mais les leviers de l'Application les appliquent via
        // DRS NVAPI / ADLX : il n'y a aucune feuille lisible simple, donc ils sont absents ici.
        _catalog.DescriptorFor("latency.renderQueueCap").Should().BeNull();
        _catalog.DescriptorFor("latency.vrrVsyncCap").Should().BeNull();
    }

    [Fact]
    public void Hags_HasHklmGraphicsDriversLeaf_MatchingTheTweak()
    {
        var d = _catalog.DescriptorFor("os.hags");

        d.Should().NotBeNull();
        d!.Surface.Should().Be(ReconcileSurface.Registry);
        d.Registry!.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        d.Registry.Path.Should().Be(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        d.Registry.Name.Should().Be("HwSchMode");
        d.Registry.Kind.Should().Be(RegistryValueType.DWord);
    }

    [Fact]
    public void Hkcu_ns1_tweaks_have_no_descriptor_localsystem_reconcile_is_hklm_only()
    {
        // Le réconciliateur du démarrage tourne en LocalSystem ; une réapplication HKCU toucherait la
        // ruche de SYSTEM, donc les leviers HKCU sont volontairement absents (la réconciliation par utilisateur reste à faire).
        _catalog.DescriptorFor("os.gameDvrBackground").Should().BeNull();
        _catalog.DescriptorFor("os.gameMode").Should().BeNull();
    }

    [Fact]
    public void UnknownTweakId_HasNoDescriptor()
        => _catalog.DescriptorFor("does.not.exist").Should().BeNull();

    [Fact]
    public void Canonicalizer_Dword_MatchesTheApplicationTweaksInvariantString()
    {
        // NvidiaUltraLowLatencyTweak écrit UltraLowLatency=2 via value.ToString(InvariantCulture).
        ReconcilableValueCanonicalizer.Dword(2u).Should().Be("2");
        ReconcilableValueCanonicalizer.Dword(0u).Should().Be("0");
        ReconcilableValueCanonicalizer.Dword(uint.MaxValue).Should().Be("4294967295");
    }

    [Fact]
    public void Canonicalizer_PowerPlan_IsLowerCaseHyphenatedNoBraces()
        => ReconcilableValueCanonicalizer.PowerPlan(KnownPowerPlans.HighPerformance)
            .Should().Be("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    [Fact]
    public void Canonicalizer_Service_IsAStableLowerCaseToken()
    {
        ReconcilableValueCanonicalizer.Service(ServiceRunState.Running).Should().Be("running");
        ReconcilableValueCanonicalizer.Service(ServiceRunState.Stopped).Should().Be("stopped");
        ReconcilableValueCanonicalizer.Service(ServiceRunState.NotInstalled).Should().Be("notinstalled");
    }
}
