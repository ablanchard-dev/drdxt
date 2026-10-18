using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// Le plafond thermique, la PL1 baissée à chaud quand la dernière partie a été freinée par la chaleur.
// Machine de référence : PL1 = PL2 = 253 W au BIOS, 110 W médians en partie ⇒ plafond 150 W.
public sealed class PlafondThermiqueCpuTweakTests
{
    private const ulong Unite = 0x3; // huitièmes de watt
    private const ulong Registre253W = (2024UL << 32) | (1UL << 47) | (1UL << 15) | 2024UL;
    private const ulong Info125W = 1000UL;

    private readonly ICpuPowerLimitReader _lecteur = Substitute.For<ICpuPowerLimitReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState Etat(LimiteDePuissanceLue? limite) =>
        new SettingsState(TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
            GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On) with { LimitePuissance = limite };

    private static HardwareInventory Hw(ChassisType chassis = ChassisType.Desktop) => new(
        new CpuInfo("Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.00", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3200, 3200) },
        new MotherboardInfo("MSI", "B760"), new BiosInfo("1.0", null, true), chassis);

    private PlafondThermiqueCpuTweak Build(double? plafond)
    {
        var lu = RegistreDeLimiteCpu.Decoder(Unite, Registre253W, Info125W)! with { CopieMmioPl1W = 0, PlafondThermiqueW = plafond };
        _lecteur.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, Registre253W, Info125W), lu)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        return new PlafondThermiqueCpuTweak(_lecteur, _elevated);
    }

    [Fact]
    public void Le_plafond_se_pose_quand_la_limite_lue_le_depasse_et_jamais_ailleurs()
    {
        var lu = new LimiteDePuissanceLue(253, 253, 125, false, PlafondThermiqueW: 150);
        PlafondThermiqueCpuTweak.Cible(lu).Should().Be(150);
        PlafondThermiqueCpuTweak.Cible(lu with { PlafondThermiqueW = null }).Should().BeNull("la dernière partie n'a pas chauffé");
        PlafondThermiqueCpuTweak.Cible(lu with { Pl1W = 152 }).Should().BeNull("à 5 W près, il ne coupe rien");
        PlafondThermiqueCpuTweak.Cible(lu with { Verrou = true }).Should().BeNull();
        PlafondThermiqueCpuTweak.Cible(lu with { Pl2W = 140 }).Should().BeNull("au-dessus de PL2 : le helper n'écrit pas cette forme");
        // Une origine que le helper refuserait de rendre (PL1 « sans limite », ou au-dessus de PL2).
        PlafondThermiqueCpuTweak.Cible(lu with { RegistreAuMaximum = true }).Should().BeNull();
        PlafondThermiqueCpuTweak.Cible(lu with { Pl1W = 300 }).Should().BeNull();
        PlafondThermiqueCpuTweak.Cible(lu with { CopieMmioPl1W = 150 }).Should().BeNull("la carte tient déjà 150 W par sa copie MMIO");

        Build(150).AppliesTo(Hw(), Etat(lu)).Should().BeTrue();
        Build(150).AppliesTo(Hw(ChassisType.Laptop), Etat(lu)).Should().BeFalse();
    }

    // Aucune température relevée en partie : on ne sait pas si le processeur se freine par la chaleur. La vérification disait
    // « déjà réglé » ; c'est « non lu ». Des parties lues sans freinage, elles, disent bien qu'il n'y a rien à plafonner.
    [Fact]
    public void Sans_temperature_relevee_en_partie_l_etat_est_dit_non_lu()
    {
        var sansPartie = new LimiteDePuissanceLue(253, 253, 125, false);

        Build(null).AppliesTo(Hw(), Etat(sansPartie)).Should().BeFalse();
        Build(null).EtatNonLu(Etat(sansPartie)).Should().BeTrue();
        Build(null).EtatNonLu(Etat(sansPartie with { ChaleurEnPartieMaxC = 82 })).Should().BeFalse("des parties lues, pas freinées");
    }

    [Fact]
    public void La_montee_s_abstient_tant_que_le_plafond_a_quelque_chose_a_dire()
    {
        var lu = new LimiteDePuissanceLue(65, 219, 65, false, ChaleurEnPartieMaxC: 80);
        LimitePuissanceCpuTweak.Cible(lu).Should().Be(219);
        LimitePuissanceCpuTweak.Cible(lu with { PlafondThermiqueW = 150 }).Should().BeNull();
    }

    [Fact]
    public async Task La_pose_n_ecrit_que_la_PL1_vers_le_bas_et_garde_l_inverse_exact()
    {
        PrivilegedOp? ecrit = null;
        var levier = Build(150);
        _elevated.ExecuteAsync(Arg.Do<PrivilegedOp>(o => ecrit = o), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));

        var r = await levier.ApplyAsync();

        r.IsOk.Should().BeTrue();
        var op = ecrit.Should().BeOfType<SetCpuPackagePowerLimitOp>().Subject;
        (op.Valeur & 0x7FFF).Should().Be(1200, "150 W en huitièmes");
        (op.Valeur >> 32 & 0x7FFF).Should().Be(2024, "PL2 intouchée");
        op.Origine.Should().Be(Registre253W);
        RegistreDeLimiteCpu.NeToucheQuePl1(op.Valeur, op.Origine).Should().BeTrue("la seule forme que le helper admet");
        RegistreDeLimiteCpu.NeToucheQuePl1(op.Origine, op.Valeur).Should().BeTrue("et le retour aussi");
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value.Should().Be(new SetCpuPackagePowerLimitOp(Registre253W, op.Valeur));
    }

    // Après un redémarrage, le BIOS a remis sa valeur : le levier n'est plus posé.
    [Fact]
    public async Task Un_plafond_efface_par_le_redemarrage_se_lit_comme_tel()
    {
        var levier = Build(150);
        var snapshot = (await levier.ApplyAsync()).Value!;
        var pose = (SetCpuPackagePowerLimitOp)PrivilegedOpCodec.Deserialize(snapshot.AppliedValueJson).Value!;

        levier.EncorePose(snapshot, [snapshot]).Should().BeFalse("le registre lu porte encore l'origine (253 W) : le plafond n'y est pas");
        var lu = RegistreDeLimiteCpu.Decoder(Unite, pose.Valeur, Info125W)!;
        _lecteur.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(Unite, pose.Valeur, Info125W), lu)));
        levier.EncorePose(snapshot, [snapshot]).Should().BeTrue();
        levier.EncorePose(snapshot with { AppliedValueJson = null }, []).Should().BeNull("pas su lire");
    }

    [Fact]
    public async Task Sans_plafond_a_la_relecture_rien_n_est_ecrit()
    {
        var levier = Build(null);
        (await levier.ApplyAsync()).IsOk.Should().BeFalse();
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
    // Sans température de partie, un verrou ou une PL1 au-dessus de la PL2 donnent déjà la réponse : pas de plafond, chaleur
    // ou pas. Ce n'est pas « non lu ».
    [Fact]
    public void Une_reponse_connue_sans_temperature_n_est_pas_dite_non_lue()
    {
        var sansPartie = new LimiteDePuissanceLue(253, 253, 125, false);

        Build(null).EtatNonLu(Etat(sansPartie with { Verrou = true })).Should().BeFalse("verrouillée");
        Build(null).EtatNonLu(Etat(sansPartie with { Pl1W = 300 })).Should().BeFalse("PL1 au-dessus de la PL2 : le helper refuserait");
        Build(null).EtatNonLu(Etat(sansPartie)).Should().BeTrue("seule la chaleur manque");
    }
}
