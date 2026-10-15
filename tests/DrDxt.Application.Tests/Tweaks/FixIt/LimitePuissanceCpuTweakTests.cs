using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// La limite de puissance du processeur Intel (PL1) montée à chaud jusqu'à la PL2 de la carte.
// Doublures NSubstitute seulement : aucune lecture ni écriture de la vraie machine.
public sealed class LimitePuissanceCpuTweakTests
{
    private const ulong Unite = 0x3; // huitièmes de watt
    private const ulong Pl2_219W = 1752UL << 32;
    private const ulong Registre65W = Pl2_219W | (1UL << 47) | (1UL << 15) | 520UL;
    private const ulong Info65W = 520UL; // puissance de base 65 W, maximale absente (0)

    private readonly ICpuPowerLimitReader _lecteur = Substitute.For<ICpuPowerLimitReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState Etat(LimiteDePuissanceLue? limite) =>
        new SettingsState(TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On) with { LimitePuissance = limite };

    private static HardwareInventory Hw(ChassisType chassis, string vendeur = "GenuineIntel") => new(
        new CpuInfo(vendeur.Contains("AMD") ? "Ryzen 5 5600" : "Core i7-14700F", vendeur, 8, 16, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B760"), new BiosInfo("1.0", null, true), chassis);

    private LimitePuissanceCpuTweak Build(ulong registre = Registre65W, ulong info = Info65W, double? mmio = 0)
    {
        var lu = RegistreDeLimiteCpu.Decoder(Unite, registre, info)! with { CopieMmioPl1W = mmio };
        _lecteur.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, registre, info), lu)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new LimitePuissanceCpuTweak(_lecteur, _elevated);
    }

    [Fact]
    public void Un_Intel_de_bureau_tenu_a_65_W_sous_une_PL2_de_219_W_est_propose() =>
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(new LimiteDePuissanceLue(65, 219, 65, false))).Should().BeTrue();

    [Theory]
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    public void Jamais_sur_un_portable_ni_une_console(ChassisType chassis) =>
        Build().AppliesTo(Hw(chassis), Etat(new LimiteDePuissanceLue(45, 125, 65, false))).Should().BeFalse();

    [Fact]
    public void Jamais_hors_Intel() =>
        Build().AppliesTo(Hw(ChassisType.Desktop, "AuthenticAMD"), Etat(new LimiteDePuissanceLue(65, 219, 65, false))).Should().BeFalse();

    // Un processeur qui touche déjà 95 °C en partie ne se voit pas monter sa PL1, quelle que soit la PL2.
    [Fact]
    public void Un_processeur_qui_se_freine_en_partie_n_est_pas_monte()
    {
        var chaud = new LimiteDePuissanceLue(65, 219, 65, false, ChaleurEnPartieMaxC: 97);
        LimitePuissanceCpuTweak.Cible(chaud).Should().BeNull();
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(chaud)).Should().BeFalse();
        LimitePuissanceCpuTweak.Cible(chaud with { ChaleurEnPartieMaxC = 88 }).Should().Be(219);
    }

    // Le guide 5.3 exige le microcode corrigé avant de monter un Core 13e/14e ; le levier ne le lisait pas.
    // La garde vit dans la lecture des registres, que ApplyAsync refait à chaque pose (essai en cours compris).
    [Fact]
    public void Jamais_plus_de_puissance_sur_un_microcode_d_avant_le_correctif_Vmin()
    {
        var lu = new LimiteDePuissanceLue(65, 219, 65, false);
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(lu with { MicrocodeVminPerime = true })).Should().BeFalse();
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(lu)).Should().BeTrue();
        LimitePuissanceCpuTweak.Cible(lu with { MicrocodeVminPerime = true }).Should().BeNull();
    }

    [Fact]
    public async Task Une_pose_relue_sur_un_microcode_perime_n_ecrit_rien()
    {
        var lu = RegistreDeLimiteCpu.Decoder(Unite, Registre65W, Info65W)! with { CopieMmioPl1W = 0, MicrocodeVminPerime = true };
        _lecteur.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, Registre65W, Info65W), lu)));
        var levier = new LimitePuissanceCpuTweak(_lecteur, _elevated);

        (await levier.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Non_lu_est_dit_non_lu_et_jamais_propose()
    {
        Build().AppliesTo(Hw(ChassisType.Desktop), Etat(null)).Should().BeFalse();
        Build().EtatNonLu(Etat(null)).Should().BeTrue();
        Build().EtatNonLu(Etat(new LimiteDePuissanceLue(65, 219, 65, false))).Should().BeFalse();
    }

    [Fact]
    public void La_cible_est_la_PL2_de_la_carte_bornee_par_le_maximum_publie_et_la_copie_MMIO_active()
    {
        LimitePuissanceCpuTweak.Cible(new LimiteDePuissanceLue(65, 219, 65, false)).Should().Be(219);
        LimitePuissanceCpuTweak.Cible(new LimiteDePuissanceLue(65, 219, 65, false, MaximaleW: 154)).Should().Be(154, "le processeur publie son maximum");
        LimitePuissanceCpuTweak.Cible(new LimiteDePuissanceLue(65, 219, 65, false, CopieMmioPl1W: 125)).Should().Be(125, "la carte tient 125 W par sa copie MMIO");
        LimitePuissanceCpuTweak.Cible(new LimiteDePuissanceLue(65, 219, 65, false, CopieMmioPl1W: 0)).Should().Be(219, "copie inactive : elle ne borne rien");
    }

    [Theory]
    [InlineData(253, 253, false, false)]  // déjà à la PL2 (la machine de référence)
    [InlineData(65, 219, true, false)]    // verrou du BIOS
    [InlineData(65, 219, false, true)]    // registre « sans limite »
    [InlineData(219, 219, false, false)]  // aucune marge
    [InlineData(65, 65, false, false)]    // PL2 = PL1 : la carte ne laisse rien passer
    public void Rien_sans_marge_sure(double pl1, double pl2, bool verrou, bool sansLimite) =>
        LimitePuissanceCpuTweak.Cible(new LimiteDePuissanceLue(pl1, pl2, 65, verrou, RegistreAuMaximum: sansLimite)).Should().BeNull();

    [Fact]
    public async Task Apply_ecrit_PL1_a_la_PL2_et_garde_l_origine_exacte_pour_le_retour()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        var attendu = RegistreDeLimiteCpu.ComposerPl1(Registre65W, 219, Unite);
        await _elevated.Received(1).ExecuteAsync(new SetCpuPackagePowerLimitOp(attendu, Registre65W), Arg.Any<CancellationToken>());
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(new SetCpuPackagePowerLimitOp(Registre65W, attendu));
        PrivilegedOpCodec.Deserialize(r.Value.AppliedValueJson).Value.Should().Be(new SetCpuPackagePowerLimitOp(attendu, Registre65W));
        RegistreDeLimiteCpu.NeToucheQuePl1(attendu, Registre65W).Should().BeTrue("la forme que le helper admet");

        await t.RevertAsync(r.Value);
        await _elevated.Received(1).ExecuteAsync(new SetCpuPackagePowerLimitOp(Registre65W, attendu), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relu_sans_marge_juste_avant_rien_n_est_ecrit()
    {
        var t = Build(registre: (Pl2_219W | (1UL << 47) | (1UL << 15) | 1752UL));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lecture_ratee_rien_n_est_ecrit()
    {
        _lecteur.LireRegistres().Returns(Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "pas de PawnIO"));
        var t = new LimitePuissanceCpuTweak(_lecteur, _elevated);

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Le_levier_se_juge_en_jeu_sur_la_surface_processeur()
    {
        ((ITweak)Build()).TakesEffectAfterReboot.Should().BeFalse();
        var d = CatalogueDeCandidats.Decrire("fixit.cpu_power_limit");
        d.Should().NotBeNull();
        d!.Kind.Should().Be(CandidateKind.AutoHot);
        d.Surface.Should().Be(SurfaceTouchee.Processeur);
        d.Family.Should().Be(CandidateFamily.Cpu);
    }
}
