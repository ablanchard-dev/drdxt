using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Privileged;

// -----------------------------------------------------------------------
// A1 — typed privileged-op contract + codec.
//
// SECURITY: the codec (de)serializes via a CLOSED discriminant enum + explicit
// switch. There is NO Newtonsoft TypeNameHandling, NO Type.GetType, NO free
// type-name resolution. An unknown discriminant / malformed JSON must fail
// CLEAN (Result.Fail("HELPER.NOT_ALLOWLISTED", ...)) and never throw, never
// instantiate an arbitrary type, never execute.
// -----------------------------------------------------------------------
public sealed class PrivilegedOpTests
{
    // --- A leaf for every closed subtype round-trips through the codec ---

    [Fact]
    public void SetRegistryValueOp_roundtrips_typed()
    {
        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "ReflexEnabled",
            RegistryValueType.DWord,
            "1");

        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();

        var back = PrivilegedOpCodec.Deserialize(json.Value!);
        back.IsOk.Should().BeTrue();
        back.Value.Should().BeOfType<SetRegistryValueOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void DeleteRegistryValueOp_roundtrips_typed()
    {
        var op = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "ReflexEnabled");

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<DeleteRegistryValueOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void SetActivePowerPlanOp_roundtrips_typed()
    {
        var op = new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance);

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<SetActivePowerPlanOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void SetWirelessPowerSavingOp_roundtrips_typed()
    {
        var op = new SetWirelessPowerSavingOp(KnownPowerPlans.Balanced, 2);

        var back = PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value!);

        back.Value.Should().BeOfType<SetWirelessPowerSavingOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void SetCpuPackagePowerLimitOp_roundtrips_typed()
    {
        var op = new SetCpuPackagePowerLimitOp((1752UL << 32) | (1UL << 15) | 1752UL, (1752UL << 32) | (1UL << 15) | 520UL);

        var back = PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value!);

        back.Value.Should().BeOfType<SetCpuPackagePowerLimitOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void SetProcessorBoostModeOp_roundtrips_typed()
    {
        var op = new SetProcessorBoostModeOp(KnownPowerPlans.Balanced, 2);

        var back = PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value!);

        back.Value.Should().BeOfType<SetProcessorBoostModeOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void CreateRestorePointOp_roundtrips_typed()
    {
        var op = new CreateRestorePointOp("DrDXT before latency batch");

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<CreateRestorePointOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void ApplyNvApiClockOffsetOp_roundtrips_typed()
    {
        var op = new ApplyNvApiClockOffsetOp(GpuIndex: 0, CoreOffsetMhz: -100, MemoryOffsetMhz: 200);

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<ApplyNvApiClockOffsetOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void ApplyNvApiPowerLimitOp_roundtrips_typed()
    {
        var op = new ApplyNvApiPowerLimitOp(GpuIndex: 0, PowerLimitPercent: 90);

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<ApplyNvApiPowerLimitOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void ApplyNvApiDrsSettingOp_roundtrips_typed()
    {
        var op = new ApplyNvApiDrsSettingOp(
            SettingId: 0x10835000u, Value: 3u, ProfileName: "Global");

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<ApplyNvApiDrsSettingOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void ApplyAdlxTuningOp_roundtrips_typed()
    {
        var op = new ApplyAdlxTuningOp(
            GpuIndex: 0, Kind: AdlxTuningKind.AntiLag, IntValue: 1);

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<ApplyAdlxTuningOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void RunServiceControlOp_roundtrips_typed()
    {
        var op = new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start);

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<RunServiceControlOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void InstallPresentMonServiceOp_roundtrips_typed()
    {
        var op = new InstallPresentMonServiceOp();

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        // Field-less record: BeOfType is the meaningful round-trip assertion.
        back.Value.Should().BeOfType<InstallPresentMonServiceOp>();
    }

    [Fact]
    public void EnableSystemRestoreOp_roundtrips_typed()
    {
        var op = new EnableSystemRestoreOp("C:");

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<EnableSystemRestoreOp>();
        back.Value.Should().BeEquivalentTo(op);
    }

    [Fact]
    public void PingOp_still_roundtrips_typed()
    {
        var op = new PingOp();

        var json = PrivilegedOpCodec.Serialize(op);
        var back = PrivilegedOpCodec.Deserialize(json.Value!);

        back.Value.Should().BeOfType<PingOp>();
    }

    // --- SECURITY: hostile / unknown payloads fail CLEAN, never execute ---

    [Fact]
    public void Deserialize_unknown_type_name_fails_clean_not_allowlisted()
    {
        // The classic RCE shape: a Newtonsoft-style "$type" with an arbitrary
        // type name. The codec must not resolve it; it must refuse.
        var act = () => PrivilegedOpCodec.Deserialize(
            """{"$type":"System.Diagnostics.Process, System","cmd":"calc"}""");

        var result = act.Should().NotThrow().Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Deserialize_unknown_discriminant_fails_clean_not_allowlisted()
    {
        var act = () => PrivilegedOpCodec.Deserialize(
            """{"op":"EvilExecOp","cmd":"calc"}""");

        var result = act.Should().NotThrow().Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Deserialize_out_of_range_discriminant_fails_clean()
    {
        // A numeric discriminant outside the closed enum.
        var act = () => PrivilegedOpCodec.Deserialize("""{"op":99999}""");

        var result = act.Should().NotThrow().Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Deserialize_missing_discriminant_fails_clean()
    {
        var act = () => PrivilegedOpCodec.Deserialize("""{"path":"whatever"}""");

        var result = act.Should().NotThrow().Subject;
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ this is : broken")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("12345")]
    public void Deserialize_malformed_json_fails_clean_no_throw(string payload)
    {
        var act = () => PrivilegedOpCodec.Deserialize(payload);

        var result = act.Should().NotThrow("a malformed payload must fail-clean, never throw").Subject;
        result.IsOk.Should().BeFalse();
    }

    // --- The closed leaf set is exactly what we expect (guard against drift) ---

    [Fact]
    public void PrivilegedOpType_has_exactly_the_closed_leaf_set()
    {
        Enum.GetNames<PrivilegedOpType>().Should().BeEquivalentTo(new[]
        {
            nameof(PrivilegedOpType.Ping),
            nameof(PrivilegedOpType.SetRegistryValue),
            nameof(PrivilegedOpType.DeleteRegistryValue),
            nameof(PrivilegedOpType.SetActivePowerPlan),
            nameof(PrivilegedOpType.CreateRestorePoint),
            nameof(PrivilegedOpType.ApplyNvApiClockOffset),
            nameof(PrivilegedOpType.ApplyNvApiPowerLimit),
            nameof(PrivilegedOpType.ApplyNvApiDrsSetting),
            nameof(PrivilegedOpType.ApplyAdlxTuning),
            nameof(PrivilegedOpType.RunServiceControl),
            nameof(PrivilegedOpType.InstallPresentMonService),
            nameof(PrivilegedOpType.ApplyGpuFanCurve),
            nameof(PrivilegedOpType.EnableSystemRestore),
            nameof(PrivilegedOpType.EstablishLease),
            nameof(PrivilegedOpType.RenewLease),
            // M3 lot 2, elargi DELIBEREMENT le 26/08. Ce test existe pour qu'on ne puisse pas
            // ajouter une op privilegiee sans le decider : il a fait son travail, il a rougi.
            // Les deux ops ci-dessous passent par shutdown.exe et n'ecrivent aucune variable
            // firmware ; elles sont bornees par l'allowlist (delai 0..300 s) et refusees comme
            // revert de bail (LeaseWatchdog), pour qu'un dead-man's switch ne puisse jamais
            // redemarrer une machine sans personne devant.
            nameof(PrivilegedOpType.RebootToFirmwareSetup),
            nameof(PrivilegedOpType.CancelPendingFirmwareBoot),
            // Elargie DELIBEREMENT le 27/08 : Lecture seule. Aucune op d'ecriture firmware
            // n'a ete ajoutee ; Setup et CpuSetup se lisent, noms fermes dans l'allowlist.
            nameof(PrivilegedOpType.ReadFirmwareVariable),
            // Elargie DELIBEREMENT le 10/09/2026 (phase 18 F1 lot 3) : l'INVERSE d'une ecriture au
            // profil d'un jeu. Portee = chemin d'executable uniquement, jamais le profil de base ;
            // documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.DeleteNvApiDrsSetting),
            // Elargie DELIBEREMENT le 11/09/2026 : le manifeste signe, ecrit par le helper eleve avec
            // la cle de la machine. Elle n'ecrit ni registre, ni pilote, ni firmware : un fichier sous
            // %ProgramData%\DrDxt, et ses entrees passent l'allowlist de ce qu'elles feront executer.
            // Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.CommitManifest),
            // Elargie DELIBEREMENT le 24/09/2026 : le plafond d'envoi QoS et son inverse. Deux
            // politiques Windows en MEMOIRE (ActiveStore : un redemarrage les efface), nom d'exe nu
            // et plafond bornes par l'allowlist. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.ApplyUploadCap),
            nameof(PrivilegedOpType.RemoveUploadCap),
            // Elargie DELIBEREMENT le 24/09/2026 : retirer / reposer un element BCD de `{current}`, parmi
            // les cinq que BcdInterpretation sait juger, valeurs documentees seulement. Aucun ne peut
            // empecher le demarrage. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.DeleteBcdValue),
            nameof(PrivilegedOpType.SetBcdValue),
            // Elargie DELIBEREMENT le 25/09/2026 : lecture de l'etat du magasin de composants
            // (DISM /CheckHealth), sans parametre, rien n'est ecrit. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.ReadComponentStoreHealth),
            // Elargie DELIBEREMENT le 28/09/2026 : la protection du ping retenue pour le service de demarrage
            // (un fichier sous %ProgramData%\DrDxt, meme borne que le plafond). Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.RetainServiceUploadCap),
            nameof(PrivilegedOpType.ForgetServiceUploadCap),
            // Elargie deliberement le 07/10/2026 (L-CPU) : le mode de boost secteur d'un plan, et rien d'autre du plan
            // (PERFBOOSTMODE, valeur 0 a 4 documentee par Microsoft). Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.SetProcessorBoostMode),
            // Elargie deliberement le 08/10/2026 (GPU-OC) : rendre les ventilateurs d'une carte a l'automatique, l'inverse
            // d'une vitesse fixe posee en mode Expert. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.RestoreGpuFanAuto),
            // Elargie deliberement le 10/10/2026 (B1) : la limite de puissance du processeur (PL1 seule, dans la PL2 de la
            // carte, sans verrou), ecrite par PawnIO dans le helper. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.SetCpuPackagePowerLimit),
            // Elargie deliberement le 10/10/2026 (B3) : l'economie d'energie de la carte sans fil (secteur) d'un plan,
            // valeur 0 a 3. Documentee dans docs/NON-NUISANCE.md.
            nameof(PrivilegedOpType.SetWirelessPowerSaving),
        });
    }

    [Fact]
    public void KnownPowerPlans_are_the_documented_windows_guids()
    {
        KnownPowerPlans.HighPerformance.Should().Be(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
        KnownPowerPlans.Balanced.Should().Be(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"));
        KnownPowerPlans.Ultimate.Should().Be(Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"));
    }
}
