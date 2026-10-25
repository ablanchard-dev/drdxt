using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

/// <summary>Le ventilateur en mode Expert. Doublures seulement : aucune carte n'est touchée.</summary>
public sealed class VentilateurExpertTests
{
    private readonly IElevatedClient _eleve = Substitute.For<IElevatedClient>();
    private readonly IRevertEnAttente _journal = Substitute.For<IRevertEnAttente>();

    public VentilateurExpertTests()
    {
        _eleve.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _journal.Poser(Arg.Any<string>(), Arg.Any<TweakSnapshot?>()).Returns(Result.Ok());
        _journal.Lever().Returns(Result.Ok());
    }

    private VentilateurExpert Ventilateur() => new(_eleve, _journal);

    private static bool EstLeRetour(string json) => PrivilegedOpCodec.Deserialize(json).Value is RestoreGpuFanAutoOp { GpuIndex: 0 };

    [Fact]
    public async Task Le_journal_puis_le_bail_puis_la_vitesse()
    {
        var r = await Ventilateur().PoserAsync(70);
        r.IsOk.Should().BeTrue();
        await using var essai = r.Value!;

        Received.InOrder(() =>
        {
            _journal.Poser(VentilateurExpert.IdLevier, Arg.Is<TweakSnapshot>(s => EstLeRetour(s.OriginalValueJson)));
            _eleve.ExecuteAsync(Arg.Is<EstablishLeaseOp>(l => l.LeaseId == VentilateurExpert.IdBail && EstLeRetour(l.RevertOpJson)), Arg.Any<CancellationToken>());
            _eleve.ExecuteAsync(Arg.Is<ApplyGpuFanCurveOp>(f => f.Points.Count == 1 && f.Points[0].DutyPct == 70), Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(39)]
    [InlineData(101)]
    public async Task Hors_des_bornes_rien_n_est_ecrit(int pourcent)
    {
        (await Ventilateur().PoserAsync(pourcent)).IsOk.Should().BeFalse();
        _journal.ReceivedCalls().Should().BeEmpty();
        _eleve.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Bail_refuse_le_ventilateur_n_est_pas_touche()
    {
        _eleve.ExecuteAsync(Arg.Any<EstablishLeaseOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "non")));
        (await Ventilateur().PoserAsync(70)).IsOk.Should().BeFalse();
        await _eleve.DidNotReceive().ExecuteAsync(Arg.Any<ApplyGpuFanCurveOp>(), Arg.Any<CancellationToken>());
        _journal.Received(1).Lever();
    }

    [Fact]
    public async Task Un_autre_essai_en_cours_rien_n_est_ecrit()
    {
        _journal.Poser(Arg.Any<string>(), Arg.Any<TweakSnapshot?>()).Returns(Result.Fail("REVERT", "gpu.undervolt est encore en essai"));
        (await Ventilateur().PoserAsync(70)).IsOk.Should().BeFalse();
        _eleve.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Rendre_remet_l_automatique_et_leve_le_journal()
    {
        await using var essai = (await Ventilateur().PoserAsync(70)).Value!;
        _journal.ClearReceivedCalls();
        (await essai.RendreAsync()).IsOk.Should().BeTrue();
        await _eleve.Received(1).ExecuteAsync(new RestoreGpuFanAutoOp(0), Arg.Any<CancellationToken>());
        _journal.Received(1).Lever();
    }

    // Rendre l'automatique pendant une pose (un autre essai qui remplace celui-ci) part hors portée : le helper ne le
    // rejouera jamais.
    [Fact]
    public async Task Rendre_dans_une_portee_de_pose_part_hors_portee()
    {
        await using var essai = (await Ventilateur().PoserAsync(70)).Value!;
        PorteeDePose? vue = null;
        _eleve.When(e => e.ExecuteAsync(new RestoreGpuFanAutoOp(0), Arg.Any<CancellationToken>()))
            .Do(_ => vue = PorteeDePose.Courante);
        var portee = PorteeDePose.Ouvrir();

        (await essai.RendreAsync()).IsOk.Should().BeTrue();

        PorteeDePose.Courante.Should().BeSameAs(portee);
        vue.Should().BeNull();
    }

    [Fact]
    public void Le_demarrage_ne_rejoue_que_le_retour_a_l_automatique()
    {
        RevertAuDemarrage.InverseRejouable(VentilateurExpert.IdLevier, PrivilegedOpCodec.Serialize(new RestoreGpuFanAutoOp(0)).Value)
            .Should().BeTrue();
        RevertAuDemarrage.InverseRejouable(VentilateurExpert.IdLevier,
            PrivilegedOpCodec.Serialize(new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(0, 100)])).Value).Should().BeFalse();
    }

    // La section s'affichait sur un portable NVIDIA (que la couverture dit exclu) et, avec deux cartes
    // NVIDIA, l'index 0 seul était piloté sans que l'écran le dise. PC fixe, une seule carte NVIDIA.
    private static HardwareInventory Machine(ChassisType chassis, params string[] vendeurs) => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        vendeurs.Select(v => new GpuInfo(v + " carte", v, 8L << 30, "1", TriState.On)).ToArray(),
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B760"), new BiosInfo("1", null, true), chassis);

    [Fact]
    public void Possible_sur_un_fixe_a_une_seule_carte_NVIDIA()
    {
        VentilateurExpert.Possible(Machine(ChassisType.Desktop, "NVIDIA")).Should().BeTrue();
        VentilateurExpert.Possible(Machine(ChassisType.Laptop, "NVIDIA")).Should().BeFalse("le constructeur pilote le ventilateur");
        VentilateurExpert.Possible(Machine(ChassisType.Desktop, "AMD")).Should().BeFalse();
        VentilateurExpert.Possible(Machine(ChassisType.Desktop, "NVIDIA", "NVIDIA")).Should().BeFalse("une seule carte est pilotée");
    }

    // Sur AMD, le ventilateur passe par ADLX ; il reste fermé tant que l'architecture de la carte n'a pas son rapport.
    [Fact]
    public void Sur_une_seule_carte_AMD_il_n_ouvre_qu_avec_un_rapport_valide()
    {
        var rx9070 = Machine(ChassisType.Desktop, "AMD") with
        {
            Gpus = [new GpuInfo("AMD Radeon RX 9070 XT", "AMD", 16L << 30, "1", TriState.On)],
        };
        IReadOnlySet<GpuArchitecture> rdna4 = new HashSet<GpuArchitecture> { GpuArchitecture.Rdna4 };

        VentilateurExpert.Possible(rx9070).Should().BeFalse("aucune architecture AMD n'a encore son rapport");
        VentilateurExpert.Possible(rx9070, rdna4).Should().BeTrue();
        VentilateurExpert.Possible(Machine(ChassisType.Laptop, "AMD") with { Gpus = rx9070.Gpus }, rdna4).Should().BeFalse();
    }
}
