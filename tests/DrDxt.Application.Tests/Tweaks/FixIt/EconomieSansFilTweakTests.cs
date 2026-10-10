using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// B3 (10/10/2026) : la carte sans fil sortie de son mode d'économie d'énergie (valeur secteur du plan actif) quand le jeu
// passe par le Wi-Fi. Doublures NSubstitute seulement : aucune lecture ni écriture de la vraie machine.
public sealed class EconomieSansFilTweakTests
{
    private static readonly Guid Plan = KnownPowerPlans.Balanced;
    private readonly IPowerPlanReader _power = Substitute.For<IPowerPlanReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static NetworkAdapterState Carte(string nom, bool filaire) =>
        new(nom, nom, filaire, TriState.Unknown, TriState.Unknown, TriState.Unknown);

    private static SettingsState Etat(uint? valeur, params NetworkAdapterState[] cartes) =>
        new SettingsState(TriState.On, TriState.On, Plan.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On) with
        {
            NetworkAdapters = cartes.Length == 0 ? null : cartes,
            EconomieSansFil = valeur is { } v ? new EconomieSansFilLue(Plan, v) : null,
        };

    private static HardwareInventory Hw(ChassisType chassis) => new(
        new CpuInfo("Core i5-12400F", "GenuineIntel", 6, 12, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 3060", "NVIDIA", 12L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B660"), new BiosInfo("1.0", null, true), chassis);

    private EconomieSansFilTweak Build(uint valeurLue = 2)
    {
        _power.GetActivePowerPlan().Returns(Result<Guid>.Ok(Plan));
        _power.LireEconomieSansFil(Plan).Returns(Result<EconomieSansFilLue>.Ok(new EconomieSansFilLue(Plan, valeurLue)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new EconomieSansFilTweak(_power, _elevated);
    }

    [Theory]
    [InlineData(ChassisType.Desktop)]
    [InlineData(ChassisType.Laptop)]
    public void En_Wi_Fi_avec_une_economie_active_c_est_propose(ChassisType chassis) =>
        Build().AppliesTo(Hw(chassis), Etat(2, Carte("Wi-Fi", false))).Should().BeTrue();

    [Fact]
    public void Jamais_sur_une_console_portable() =>
        Build().AppliesTo(Hw(ChassisType.Handheld), Etat(2, Carte("Wi-Fi", false))).Should().BeFalse();

    [Fact]
    public void Deja_en_performances_maximales_rien() =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(0, Carte("Wi-Fi", false))).Should().BeFalse();

    [Fact]
    public void Un_cable_branche_le_jeu_passe_par_lui_rien()
    {
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(2, Carte("Ethernet", true))).Should().BeFalse("pas de Wi-Fi");
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(2, Carte("Ethernet", true), Carte("Wi-Fi", false))).Should().BeFalse("le câble porte le jeu");
    }

    [Fact]
    public void Non_lu_est_dit_non_lu()
    {
        var t = Build();
        t.AppliesTo(Hw(ChassisType.Desktop), Etat(null, Carte("Wi-Fi", false))).Should().BeFalse();
        t.EtatNonLu(Etat(null, Carte("Wi-Fi", false))).Should().BeTrue("valeur du plan non lue");
        t.EtatNonLu(Etat(2)).Should().BeTrue("cartes réseau non lues");
        t.EtatNonLu(Etat(2, Carte("Wi-Fi", false))).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_pose_performances_maximales_sur_secteur_et_garde_l_origine()
    {
        var t = Build(valeurLue: 2);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        await _elevated.Received(1).ExecuteAsync(new SetWirelessPowerSavingOp(Plan, 0), Arg.Any<CancellationToken>());
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(new SetWirelessPowerSavingOp(Plan, 2));
        PrivilegedOpCodec.Deserialize(r.Value.AppliedValueJson).Value.Should().Be(new SetWirelessPowerSavingOp(Plan, 0));

        await t.RevertAsync(r.Value);
        await _elevated.Received(1).ExecuteAsync(new SetWirelessPowerSavingOp(Plan, 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relu_deja_au_maximum_juste_avant_rien_n_est_ecrit()
    {
        var t = Build(valeurLue: 0);
        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Le_levier_est_un_reglage_pose_et_relu_de_famille_reseau()
    {
        ((ITweak)Build()).TakesEffectAfterReboot.Should().BeFalse();
        var d = CatalogueDeCandidats.Decrire("fixit.wifi_performances");
        d.Should().NotBeNull();
        d!.Kind.Should().Be(CandidateKind.AutoReglage);
        d.Family.Should().Be(CandidateFamily.Network);
    }
}
