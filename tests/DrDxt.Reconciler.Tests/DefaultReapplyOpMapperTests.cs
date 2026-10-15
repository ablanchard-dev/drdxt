using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// DefaultReapplyOpMapper : la frontière de sécurité du réconciliateur (la leçon
// WinRing0). Il résout un tweakId réconciliable + une valeur voulue sérialisée vers une
// des feuilles PrivilegedOp scellées existantes, aux champs bornés, en prenant toujours la
// cible de l'op (ruche/chemin/nom/serviceName) dans le catalogue de descripteurs, jamais
// dans la valeur fournie par l'appelant. Un tweakId inconnu, une valeur inanalysable ou
// d'un mauvais type, ou une surface non supportée → Fail avant toute production d'op.
//
// Ces tests prouvent : le chemin nominal de chaque surface (champs de l'op == descripteur +
// valeur canonique), les rejets d'analyse, la correspondance jeton de service → action (y
// compris le refus d'install / notinstalled), et la garantie dure qu'une valeur voulue
// hostile ne peut pas déplacer la cible de l'op, seulement son champ de valeur borné.
// -----------------------------------------------------------------------
public sealed class DefaultReapplyOpMapperTests
{
    private readonly DefaultReconcilableTweakCatalog _catalog = new();
    private readonly DefaultReapplyOpMapper _mapper;

    public DefaultReapplyOpMapperTests()
        => _mapper = new DefaultReapplyOpMapper(_catalog);

    // ---- Surface registre ----------------------------------------------------

    [Fact]
    public void Registry_Dword_MapsToSetRegistryValueOp_WithDescriptorTargetAndCanonicalValue()
    {
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "2");

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        // La cible vient entièrement du descripteur (pas de la valeur voulue).
        op.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        op.Path.Should().Be(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak");
        op.Name.Should().Be("LowLatencyMode");
        op.Kind.Should().Be(RegistryValueType.DWord);
        // La valeur est la forme DWord canonique (identique à l'octet près à ce que la sonde lit).
        op.Value.Should().Be("2");
    }

    [Fact]
    public void Registry_Hags_MapsToHklmHwSchModeOp_FromDescriptor()
    {
        var result = _mapper.Map("os.hags", "2");

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        op.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        op.Path.Should().Be(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        op.Name.Should().Be("HwSchMode");
        op.Kind.Should().Be(RegistryValueType.DWord);
        op.Value.Should().Be("2");
    }

    [Fact]
    public void HkcuNs1Tweak_HasNoDescriptor_SoTheMapperRefusesIt()
    {
        // os.gameDvrBackground est HKCU et volontairement non réconciliable par le réconciliateur
        // LocalSystem du démarrage → pas de descripteur → le mappeur échoue avant toute op.
        _mapper.Map("os.gameDvrBackground", "0").IsOk.Should().BeFalse();
    }

    [Fact]
    public void Registry_Dword_ReSerializesThroughCanonicalizer_SoLeadingZerosAreNormalized()
    {
        // « 007 » s'analyse comme le uint 7 → l'op porte le « 7 » canonique, celui que lit la sonde.
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "007");

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be("7");
    }

    [Fact]
    public void Registry_Dword_RejectsNonNumericValue_NoOp()
    {
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "not-a-number");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Message.Should().Contain("DWord");
    }

    [Fact]
    public void Registry_Dword_RejectsNegativeAndOverflow_NoOp()
    {
        // -1 n'est pas un uint valide ; 4294967296 déborde du uint.
        _mapper.Map("latency.reflex.lowLatencyMode", "-1").IsOk.Should().BeFalse();
        _mapper.Map("latency.reflex.lowLatencyMode", "4294967296").IsOk.Should().BeFalse();
    }

    // ---- Surface plan d'alimentation -----------------------------------------

    [Fact]
    public void PowerPlan_ValidGuid_MapsToSetActivePowerPlanOp()
    {
        var guid = KnownPowerPlans.HighPerformance;
        var result = _mapper.Map("fixit.sanePowerPlan",
            ReconcilableValueCanonicalizer.PowerPlan(guid));

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeOfType<SetActivePowerPlanOp>()
            .Which.PlanGuid.Should().Be(guid);
    }

    [Fact]
    public void PowerPlan_InvalidGuid_RejectsNoOp()
    {
        var result = _mapper.Map("fixit.sanePowerPlan", "not-a-guid");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Message.Should().Contain("GUID");
    }

    // ---- Surface service -----------------------------------------------------

    [Fact]
    public void Service_RunningToken_MapsToStartActionOnDescriptorService()
    {
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.Running));

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<RunServiceControlOp>().Subject;
        op.ServiceName.Should().Be("AMD3DVCacheSvc");
        op.Action.Should().Be(ServiceAction.Start);
    }

    [Fact]
    public void Service_StoppedToken_MapsToStopActionOnDescriptorService()
    {
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.Stopped));

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<RunServiceControlOp>().Subject;
        op.ServiceName.Should().Be("AMD3DVCacheSvc");
        op.Action.Should().Be(ServiceAction.Stop);
    }

    [Fact]
    public void Service_NotInstalledToken_Rejects_NeverInstalls()
    {
        // Le réconciliateur n'installe ni ne désinstalle en réparation de dérive (l'installation
        // relève de l'application) ; ServiceAction.Install ne doit jamais sortir du mappeur.
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.NotInstalled));

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Service_UnknownToken_Rejects()
        => _mapper.Map("fixit.x3dVCacheService", "garbage").IsOk.Should().BeFalse();

    // ---- Identifiant inconnu / surfaces non supportées -----------------------

    [Fact]
    public void UnknownTweakId_Rejects_BeforeAnythingElse()
    {
        var result = _mapper.Map("does.not.exist", "anything");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.TweakNotAllowlisted);
    }

    [Fact]
    public void DrsBackedLatencyId_Rejects_NoOpLeafInV1()
    {
        // latency.renderQueueCap est classé Registry par la politique mais appliqué via DRS NVAPI ;
        // absent du catalogue, le mappeur le traite comme inconnu.
        _mapper.Map("latency.renderQueueCap", "1").IsOk.Should().BeFalse();
        _mapper.Map("latency.vrrVsyncCap", "1").IsOk.Should().BeFalse();
    }

    // ---- Défense en profondeur : une valeur hostile ne peut pas déplacer la cible --------

    [Fact]
    public void HostileDesiredValue_CannotChangeRegistryTarget_OnlyTheBoundedValue()
    {
        // Une valeur voulue qui tente de glisser une autre ruche, un autre chemin ou un autre nom
        // (en JSON ou en texte délimité) ne doit pas influencer la cible de l'op. Pour une feuille
        // DWord elle échoue simplement à l'analyse → aucune op ; la cible n'est jamais au choix de l'attaquant.
        var hostile = "{\"Hive\":1,\"Path\":\"SYSTEM\\\\EVIL\",\"Name\":\"Pwned\",\"Value\":\"2\"}";

        var result = _mapper.Map("latency.reflex.lowLatencyMode", hostile);

        // Pas un DWord valide → rejeté net (aucune op produite).
        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void EvenWithAParseableValue_TheTargetIsAlwaysTheDescriptors_NotTheInput()
    {
        // La valeur voulue est un DWord parfaitement valide (« 2 »), donc une op est produite, mais
        // chaque champ de cible vient toujours du descripteur, jamais de l'appelant.
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "2");

        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        var descriptor = _catalog.DescriptorFor("latency.reflex.lowLatencyMode")!.Registry!;
        op.Hive.Should().Be(descriptor.Hive);
        op.Path.Should().Be(descriptor.Path);
        op.Name.Should().Be(descriptor.Name);
        op.Kind.Should().Be(descriptor.Kind);
    }
}
