using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// La limite PL1 posée par fixit.cpu_power_limit ou fixit.cpu_power_cap rendait « non vu » : la vérification ne relisait pas
// le registre, et ces leviers ne pouvaient jamais être « validés ». Seul le champ PL1 (bits 14:0), le seul que le levier
// écrit, est comparé à la pose et à l'origine, comme LevierPl1TweakBase.EncorePose.
public sealed class RelectureDeLaLimiteCpuTests
{
    private const ulong Unite = 0x3;
    private const ulong Origine65W = (1UL << 15) | 520UL;      // PL1 520/8 = 65 W
    private const ulong Pose219W = (1UL << 15) | 1752UL;       // PL1 1752/8 = 219 W
    private readonly ICpuPowerLimitReader _cpu = Substitute.For<ICpuPowerLimitReader>();

    private ValidationDeLaMachine Machine() => new(Substitute.For<ITweakCatalog>(), Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(), Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
        Substitute.For<IPowerPlanReader>(), Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>(),
        cpu: _cpu);

    private static TweakSnapshot Snapshot() => new("fixit.cpu_power_limit",
        PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(Origine65W, Pose219W)).Value!,
        PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(Pose219W, Origine65W)).Value!);

    private void Registre(ulong pkgPowerLimit) => _cpu.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(
        new LectureLimiteCpu(new RegistresCpuLus(Unite, pkgPowerLimit, 520UL), new LimiteDePuissanceLue(0, 0, 65, false))));

    [Theory]
    [InlineData(Pose219W, true)]
    [InlineData(Origine65W, false)]
    [InlineData((1UL << 15) | 800UL, null)]
    // Le bit de serrage (16) et la PL2 (bits 46:32) diffèrent de la pose, le champ PL1 est le même : posée.
    [InlineData(Pose219W | (1UL << 16) | (1000UL << 32), true)]
    public void Le_champ_PL1_du_registre_dit_si_la_limite_est_posee_ou_rendue(ulong registre, bool? posee)
    {
        Registre(registre);

        Machine().Relire(Snapshot()).Pose.Should().Be(posee);
    }

    [Fact]
    public void Registre_illisible_ne_conclut_rien()
    {
        _cpu.LireRegistres().Returns(Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "PawnIO absent"));

        Machine().Relire(Snapshot()).Pose.Should().BeNull();
    }
    // Le service du composite X3D se relit aussi dans la vérification : sans lecteur de services, Unavailable.
    [Fact]
    public void La_verification_relit_un_service_par_son_etat()
    {
        var services = Substitute.For<IServiceStateReader>();
        services.GetServiceState("AMD3DVCacheSvc").Returns(Result<ServiceState>.Ok(ServiceState.Running));
        var machine = new ValidationDeLaMachine(Substitute.For<ITweakCatalog>(), Substitute.For<IHardwareDetector>(),
            Substitute.For<ISettingsStateReader>(), Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
            Substitute.For<IPowerPlanReader>(), Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>(),
            services: services);
        var s = new TweakSnapshot("fixit.x3d_ccd",
            PrivilegedOpCodec.Serialize(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Stop)).Value!,
            PrivilegedOpCodec.Serialize(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start)).Value!);

        machine.Relire(s).Pose.Should().BeTrue();
    }
    // Une valeur à supprimer (l'origine « absente » d'un exercice) : la relecture dit « supprimée », pas « demandé ? ».
    [Fact]
    public void Une_suppression_demandee_se_dit_supprimee()
    {
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Result<uint?>.Ok(null));
        var machine = new ValidationDeLaMachine(Substitute.For<ITweakCatalog>(), Substitute.For<IHardwareDetector>(),
            Substitute.For<ISettingsStateReader>(), registre, Substitute.For<INvApi>(),
            Substitute.For<IPowerPlanReader>(), Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>());
        const string Chemin = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        var s = new TweakSnapshot("latency.hagsOn",
            PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode", RegistryValueType.DWord, "1")).Value!,
            PrivilegedOpCodec.Serialize(new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode")).Value!);

        var lu = machine.Relire(s);

        lu.Pose.Should().BeTrue();
        lu.Texte.Should().Contain(Docteur.T("VM.Supprimee")).And.NotContain("?");
        lu.Lu.Should().Be(Docteur.T("TE.Absente"));
    }
    // Une relecture qui ne sait pas lire (Unavailable) n'a pas de valeur lue : « lu absente » serait faux.
    [Fact]
    public void Une_relecture_impossible_n_a_pas_de_valeur_lue()
    {
        var machine = new ValidationDeLaMachine(Substitute.For<ITweakCatalog>(), Substitute.For<IHardwareDetector>(),
            Substitute.For<ISettingsStateReader>(), Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
            Substitute.For<IPowerPlanReader>(), Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>());
        // Un QWORD : aucune relecture ne le sait.
        static string Qword(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\X", "Y", RegistryValueType.QWord, v)).Value!;

        var lu = machine.Relire(new TweakSnapshot("levier", Qword("1"), Qword("2")));

        lu.Pose.Should().BeNull();
        lu.Lu.Should().BeNull();
        lu.Texte.Should().Contain(DrDxt.Application.Tweaks.EtatApplique.Unavailable.ToString());
    }
}
