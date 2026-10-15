using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

/// <summary>« Améliorer la précision du pointeur » coupée, trois valeurs REG_SZ de la session lues
/// avant, rendues à l'identique (absentes ⇒ supprimées), effet à la prochaine ouverture de session.</summary>
public sealed class AccelerationSourisOffTweakTests
{
    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState State(TriState? acceleration) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On, AccelerationSouris: acceleration);

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("CPU", "Intel", 8, 16, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "1.0", TriState.On) },
        new[] { new MemoryModule("PN", "Kit", 6000, 6000) },
        new MotherboardInfo("Carte", "Modèle"), new BiosInfo("1.00", null, true), ChassisType.Desktop);

    private AccelerationSourisOffTweak Build(string? speed = "1", string? seuil1 = "6", string? seuil2 = "10")
    {
        _registry.ReadString(RegistryHiveKind.CurrentUser, AccelerationSourisOffTweak.Chemin, "MouseSpeed").Returns(Result<string?>.Ok(speed));
        _registry.ReadString(RegistryHiveKind.CurrentUser, AccelerationSourisOffTweak.Chemin, "MouseThreshold1").Returns(Result<string?>.Ok(seuil1));
        _registry.ReadString(RegistryHiveKind.CurrentUser, AccelerationSourisOffTweak.Chemin, "MouseThreshold2").Returns(Result<string?>.Ok(seuil2));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new AccelerationSourisOffTweak(_registry, _elevated);
    }

    [Fact]
    public void Proposee_seulement_quand_l_acceleration_est_lue_active()
    {
        var t = Build();
        t.AppliesTo(Hw, State(TriState.On)).Should().BeTrue();
        t.AppliesTo(Hw, State(TriState.Off)).Should().BeFalse();
        t.AppliesTo(Hw, State(TriState.Unknown)).Should().BeFalse("non lu n'est pas un défaut");
        t.AppliesTo(Hw, State(null)).Should().BeFalse();
        t.EtatNonLu(State(null)).Should().BeTrue();
        t.EtatNonLu(State(TriState.Off)).Should().BeFalse();
        t.TakesEffectAfterReboot.Should().BeTrue("Windows relit ces valeurs à l'ouverture de session");
    }

    [Fact]
    public async Task Pose_ecrit_les_trois_valeurs_a_zero_en_REG_SZ_et_garde_les_origines_exactes()
    {
        var r = await Build().ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        var ecrits = _elevated.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<SetRegistryValueOp>().ToList();
        ecrits.Select(o => (o.Name, o.Kind, o.Value)).Should().BeEquivalentTo(new[]
        {
            ("MouseSpeed", RegistryValueType.String, "0"),
            ("MouseThreshold1", RegistryValueType.String, "0"),
            ("MouseThreshold2", RegistryValueType.String, "0"),
        });
        ecrits.Should().OnlyContain(o => o.Hive == RegistryHiveKind.CurrentUser && o.Path == AccelerationSourisOffTweak.Chemin);

        var inverses = CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson).Value!;
        inverses.OfType<SetRegistryValueOp>().Select(o => (o.Name, o.Value)).Should().BeEquivalentTo(new[]
        {
            ("MouseSpeed", "1"), ("MouseThreshold1", "6"), ("MouseThreshold2", "10"),
        });
    }

    [Fact]
    public async Task Une_valeur_absente_a_l_origine_se_rend_par_une_suppression()
    {
        var r = await Build(seuil2: null).ApplyAsync();

        var inverses = CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson).Value!;
        inverses.OfType<DeleteRegistryValueOp>().Should().ContainSingle(o => o.Name == "MouseThreshold2");
    }

    [Fact]
    public async Task Rendre_rejoue_les_origines()
    {
        var t = Build();
        var pose = await t.ApplyAsync();
        _elevated.ClearReceivedCalls();

        (await t.RevertAsync(pose.Value!)).IsOk.Should().BeTrue();

        _elevated.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<SetRegistryValueOp>()
            .Select(o => (o.Name, o.Value)).Should().BeEquivalentTo(new[] { ("MouseSpeed", "1"), ("MouseThreshold1", "6"), ("MouseThreshold2", "10") });
    }

    [Fact]
    public async Task Une_origine_illisible_n_ecrit_rien()
    {
        _registry.ReadString(RegistryHiveKind.CurrentUser, AccelerationSourisOffTweak.Chemin, Arg.Any<string>())
            .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "refusé"));
        var t = new AccelerationSourisOffTweak(_registry, _elevated);

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
}
