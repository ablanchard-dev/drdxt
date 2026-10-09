using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// R1 — the real IPrivilegedOpExecutor dispatch logic, with fake action seams.
//
// What is unit-tested here is the DISPATCH: which typed (already-allowlisted) op
// maps to which Windows action, with the right arguments. The SAFE non-GPU ops, the
// SAFE NVAPI DRS surface, the RISKY NVAPI clock-offset / power-limit writes AND the
// RISKY ADLX (AMD) tuning write are all routed to their seam (T3). The GPU fan-curve
// remains the only DEFERRED leaf (T6) — refused with an HONEST HELPER.OP_FAILED message
// (names the fan-curve, "no fan write"), NO partial action; E4 fan-write lands later.
// The native calls themselves are the m2smoke boundary, never run here.
// -----------------------------------------------------------------------
public sealed class PrivilegedOpExecutorTests
{
    private sealed class RecordingRegistry : IRegistryActions
    {
        public (RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)? Set;
        public (RegistryHiveKind hive, string path, string name)? Deleted;
        private readonly Result _result;
        public RecordingRegistry(Result? result = null) => _result = result ?? Result.Ok();

        public Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)
        {
            Set = (hive, path, name, kind, value);
            return _result;
        }

        public Result DeleteValue(RegistryHiveKind hive, string path, string name)
        {
            Deleted = (hive, path, name);
            return _result;
        }
    }

    private sealed class RecordingPower : IPowerPlanActions
    {
        public Guid? Applied;
        private readonly Result _result;
        public RecordingPower(Result? result = null) => _result = result ?? Result.Ok();
        public Result SetActivePlan(Guid planGuid) { Applied = planGuid; return _result; }
    }

    private sealed class RecordingRestore : IRestorePointActions
    {
        public string? Description;
        public string? EnabledDrive;
        private readonly Result _result;
        public RecordingRestore(Result? result = null) => _result = result ?? Result.Ok();
        public Result CreatePoint(string description) { Description = description; return _result; }
        public Result EnableSystemRestore(string driveLetter) { EnabledDrive = driveLetter; return _result; }
    }

    private sealed class RecordingService : IServiceActions
    {
        public (string name, ServiceAction action)? Controlled;
        public bool PresentMonInstalled;
        private readonly Result _result;
        public RecordingService(Result? result = null) => _result = result ?? Result.Ok();
        public Result Control(string serviceName, ServiceAction action) { Controlled = (serviceName, action); return _result; }
        public Result InstallPresentMonService() { PresentMonInstalled = true; return _result; }
    }

    private sealed class ThrowingRegistry : IRegistryActions
    {
        public Result SetValue(RegistryHiveKind h, string p, string n, RegistryValueType k, string v) => throw new InvalidOperationException("boom");
        public Result DeleteValue(RegistryHiveKind h, string p, string n) => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingDrs : INvApiDrsActions
    {
        public (uint settingId, uint value)? Applied;
        // L2 : la PORTEE reellement honoree. Un test qui ne regarde que `Applied` ne verrait pas
        // la difference entre une ecriture par jeu et une ecriture machine entiere.
        public (string path, uint settingId, uint value)? AppliedToApplication;
        private readonly Result _result;
        public RecordingDrs(Result? result = null) => _result = result ?? Result.Ok();
        public Result SetGlobalSetting(uint settingId, uint value) { Applied = (settingId, value); return _result; }
        public Result SetSettingForApplication(string applicationPath, uint settingId, uint value)
        { AppliedToApplication = (applicationPath, settingId, value); return _result; }
        // F1 lot 3 : la suppression est une methode A part — un routage vers SetSetting* ne doit
        // pas pouvoir passer pour elle.
        public (string path, uint settingId, DrsValueOrigin origine)? Deleted;
        public Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue)
        { Deleted = (applicationPath, settingId, origineAttendue); return _result; }
    }

    private sealed class RecordingTuning : INvApiTuningActions
    {
        public (int gpuIndex, int offsetMhz)? AppliedOffset;
        public (int gpuIndex, float pct)? AppliedPower;
        private readonly Result _result;
        public RecordingTuning(Result? result = null) => _result = result ?? Result.Ok();
        public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => Result<int>.Ok(0);
        public Result<int> GetPowerLimitPercent(int gpuIndex) => Result<int>.Ok(100);
        public Result SetCoreClockOffset(int gpuIndex, int offsetMHz) { AppliedOffset = (gpuIndex, offsetMHz); return _result; }
        public Result SetPowerLimitPercent(int gpuIndex, float pct) { AppliedPower = (gpuIndex, pct); return _result; }
    }

    private sealed class RecordingAdlx : IADLXTuningActions
    {
        public (int gpuIndex, AdlxTuningKind kind, int intValue)? Applied;
        private readonly Result _result;
        public RecordingAdlx(Result? result = null) => _result = result ?? Result.Ok();
        public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue)
        {
            Applied = (gpuIndex, kind, intValue);
            return _result;
        }
    }

    private sealed class RecordingFirmwareBoot : IFirmwareBootActions
    {
        public int? RebootDelay { get; private set; }
        public bool CancelCalled { get; private set; }
        public (string Name, string Guid)? LastRead { get; private set; }

        public Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid)
        {
            LastRead = (name, vendorGuid);
            return Result<byte[]>.Ok(new byte[] { 0x01 });
        }

        public Result RequestFirmwareSetupReboot(int delaySeconds)
        {
            RebootDelay = delaySeconds;
            return Result.Ok();
        }

        public Result CancelPendingFirmwareBoot()
        {
            CancelCalled = true;
            return Result.Ok();
        }
    }

    private static PrivilegedOpExecutor Build(
        IRegistryActions? reg = null, IPowerPlanActions? power = null,
        IRestorePointActions? restore = null, IServiceActions? service = null,
        INvApiDrsActions? drs = null, INvApiTuningActions? tuning = null,
        IADLXTuningActions? adlx = null, IFirmwareBootActions? firmware = null) =>
        new(reg ?? new RecordingRegistry(), power ?? new RecordingPower(),
            restore ?? new RecordingRestore(), service ?? new RecordingService(),
            drs ?? new RecordingDrs(), tuning ?? new RecordingTuning(),
            adlx ?? new RecordingAdlx(), firmware);

    // --- M3 lot 2 : le trajet vers le BIOS, et l'hote qui n'a pas le droit de le faire ---

    [Fact]
    public async Task Firmware_reboot_passes_the_delay_to_the_seam()
    {
        var fw = new RecordingFirmwareBoot();
        var exec = Build(firmware: fw);

        var r = await exec.ExecuteAsync(new RebootToFirmwareSetupOp(15), default);

        r.IsOk.Should().BeTrue();
        fw.RebootDelay.Should().Be(15);
    }

    [Fact]
    public async Task Cancelling_reaches_the_seam()
    {
        var fw = new RecordingFirmwareBoot();
        var exec = Build(firmware: fw);

        var r = await exec.ExecuteAsync(new CancelPendingFirmwareBootOp(), default);

        r.IsOk.Should().BeTrue();
        fw.CancelCalled.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_without_the_firmware_seam_REFUSES_and_says_so()
    {
        // Le ReconcilerHost tourne sans surveillance : il ne passe deliberement pas cette
        // couture. Un redemarrage qu'il aurait declenche arriverait chez le client comme une
        // machine qui repart dans le BIOS toute seule, sans rien a l'ecran pour l'expliquer.
        // L'absence doit donc echouer FORT, jamais ne rien faire en silence.
        var exec = Build();   // firmware == null, comme dans le ReconcilerHost

        var r = await exec.ExecuteAsync(new RebootToFirmwareSetupOp(0), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        r.Error.Value.Message.Should().Contain("not wired");
    }

    // --- Registry ---

    [Fact]
    public async Task SetRegistryValue_calls_registry_set_with_exact_leaf()
    {
        var reg = new RecordingRegistry();
        var exec = Build(reg: reg);

        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "LowLatencyMode", RegistryValueType.DWord, "2");
        var r = await exec.ExecuteAsync(op, default);

        r.IsOk.Should().BeTrue();
        reg.Set.Should().Be((RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode",
            RegistryValueType.DWord, "2"));
    }

    [Fact]
    public async Task DeleteRegistryValue_calls_registry_delete_with_exact_leaf()
    {
        var reg = new RecordingRegistry();
        var exec = Build(reg: reg);

        var op = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode");
        var r = await exec.ExecuteAsync(op, default);

        r.IsOk.Should().BeTrue();
        reg.Deleted.Should().Be((RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode"));
    }

    [Fact]
    public async Task Registry_action_failure_surfaces_as_op_failed()
    {
        var reg = new RecordingRegistry(Result.Fail(ErrorCodes.HelperOpFailed, "access denied"));
        var exec = Build(reg: reg);

        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "2");
        var r = await exec.ExecuteAsync(op, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Action_that_throws_is_wrapped_as_op_failed_never_propagates()
    {
        var exec = Build(reg: new ThrowingRegistry());
        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "2");

        Func<Task> act = async () => await exec.ExecuteAsync(op, default);
        await act.Should().NotThrowAsync();

        var r = await exec.ExecuteAsync(op, default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // --- Power plan ---

    [Fact]
    public async Task SetActivePowerPlan_calls_power_action_with_guid()
    {
        var power = new RecordingPower();
        var exec = Build(power: power);

        var r = await exec.ExecuteAsync(new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), default);

        r.IsOk.Should().BeTrue();
        power.Applied.Should().Be(KnownPowerPlans.HighPerformance);
    }

    // --- Restore point ---

    [Fact]
    public async Task CreateRestorePoint_calls_restore_action_with_description()
    {
        var restore = new RecordingRestore();
        var exec = Build(restore: restore);

        var r = await exec.ExecuteAsync(new CreateRestorePointOp("DrDXT before batch"), default);

        r.IsOk.Should().BeTrue();
        restore.Description.Should().Be("DrDXT before batch");
    }

    [Fact]
    public async Task EnableSystemRestore_calls_restore_action_with_drive()
    {
        var restore = new RecordingRestore();
        var exec = Build(restore: restore);

        var r = await exec.ExecuteAsync(new EnableSystemRestoreOp("C:"), default);

        r.IsOk.Should().BeTrue();
        restore.EnabledDrive.Should().Be("C:");
    }

    [Fact]
    public async Task EnableSystemRestore_failure_surfaces_as_op_failed()
    {
        var restore = new RecordingRestore(Result.Fail(ErrorCodes.HelperOpFailed, "GPO locked"));
        var exec = Build(restore: restore);

        var r = await exec.ExecuteAsync(new EnableSystemRestoreOp("C:"), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // --- Service control + PresentMon install ---

    [Fact]
    public async Task RunServiceControl_calls_service_action()
    {
        var service = new RecordingService();
        var exec = Build(service: service);

        var r = await exec.ExecuteAsync(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start), default);

        r.IsOk.Should().BeTrue();
        service.Controlled.Should().Be(("AMD3DVCacheSvc", ServiceAction.Start));
    }

    [Fact]
    public async Task InstallPresentMonService_calls_service_install()
    {
        var service = new RecordingService();
        var exec = Build(service: service);

        var r = await exec.ExecuteAsync(new InstallPresentMonServiceOp(), default);

        r.IsOk.Should().BeTrue();
        service.PresentMonInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task Ping_is_a_no_op_success()
    {
        var r = await Build().ExecuteAsync(new PingOp(), default);
        r.IsOk.Should().BeTrue();
    }

    // --- DRS driver setting: Real now (SAFE NVAPI surface, no voltage/clock) ---

    [Fact]
    public async Task ApplyNvApiDrsSetting_calls_drs_action_with_id_and_value()
    {
        var drs = new RecordingDrs();
        var exec = Build(drs: drs);

        // The corrected FRL_FPS id.
        var r = await exec.ExecuteAsync(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, "Base Profile"), default);

        r.IsOk.Should().BeTrue();
        drs.Applied.Should().Be((0x10835002u, 141u));
        drs.AppliedToApplication.Should().BeNull("« Base Profile » est la portee MACHINE ENTIERE");
    }

    // --- L2 : la PORTEE decide de la methode appelee -------------------------------------------

    [Fact]
    public async Task ApplyNvApiDrsSetting_with_a_game_path_writes_the_GAME_profile_not_the_machine()
    {
        // Le test qui manquait, ecrit apres qu'une mutation ait survecu : remplacer tout ce
        // switch par un simple SetGlobalSetting ne faisait rougir personne. C'est pourtant le
        // defaut d'origine dans sa forme exacte -- l'allowlist accepte une portee par jeu, et
        // l'executeur ecrit pour toute la machine, en silence.
        var drs = new RecordingDrs();
        var exec = Build(drs: drs);
        const string game = @"C:\Games\Call of Duty\cod.exe";

        var r = await exec.ExecuteAsync(new ApplyNvApiDrsSettingOp(0x10835002u, 197u, game), default);

        r.IsOk.Should().BeTrue();
        drs.AppliedToApplication.Should().Be((game, 0x10835002u, 197u));
        drs.Applied.Should().BeNull("aucune ecriture machine entiere ne doit partir pour une portee par jeu");
    }

    [Fact]
    public async Task DeleteNvApiDrsSetting_routes_to_the_DELETE_action_with_the_expected_origin()
    {
        // F1 lot 3 : la suppression est l'inverse d'une ecriture au jeu. Routee vers une
        // ecriture, le revert « reussirait » en laissant au jeu un reglage qu'il ne possedait pas.
        var drs = new RecordingDrs();
        var exec = Build(drs: drs);
        const string game = @"C:\Games\Call of Duty\cod.exe";

        var r = await exec.ExecuteAsync(new DeleteNvApiDrsSettingOp(0x1057EB71u, game,
            DrsValueOrigin.InheritedFromGlobalProfile, 0), default);

        r.IsOk.Should().BeTrue();
        drs.Deleted.Should().Be((game, 0x1057EB71u, DrsValueOrigin.InheritedFromGlobalProfile));
        drs.AppliedToApplication.Should().BeNull("un revert par suppression ne reecrit rien au jeu");
        drs.Applied.Should().BeNull("ni au profil de base");
    }

    [Fact]
    public async Task ApplyNvApiDrsSetting_surfaces_unavailable_drs_as_op_failed()
    {
        // AMD / no-NVIDIA box: the DRS action fails clean; the executor surfaces it.
        var drs = new RecordingDrs(Result.Fail(ErrorCodes.HelperOpFailed, "NVIDIA NVAPI unavailable"));
        var exec = Build(drs: drs);

        var r = await exec.ExecuteAsync(new ApplyNvApiDrsSettingOp(0x007BA09Eu, 1u, "Base Profile"), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // --- RISKY NVAPI clock-offset + power-limit: Real now (R2), routed to the tuning seam ---

    [Fact]
    public async Task ApplyNvApiClockOffset_calls_tuning_set_core_offset()
    {
        var tuning = new RecordingTuning();
        var exec = Build(tuning: tuning);

        var r = await exec.ExecuteAsync(new ApplyNvApiClockOffsetOp(0, -120, 0), default);

        r.IsOk.Should().BeTrue();
        tuning.AppliedOffset.Should().Be((0, -120));
    }

    [Fact]
    public async Task ApplyNvApiClockOffset_surfaces_tuning_failure_as_op_failed()
    {
        // AMD / no-NVIDIA box, or missing private SetPstates20 id: fail-clean.
        var tuning = new RecordingTuning(Result.Fail(ErrorCodes.HelperOpFailed, "SetPstates20 unavailable"));
        var exec = Build(tuning: tuning);

        var r = await exec.ExecuteAsync(new ApplyNvApiClockOffsetOp(0, -120, 0), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task ApplyNvApiPowerLimit_calls_tuning_set_power_limit()
    {
        var tuning = new RecordingTuning();
        var exec = Build(tuning: tuning);

        var r = await exec.ExecuteAsync(new ApplyNvApiPowerLimitOp(0, 90), default);

        r.IsOk.Should().BeTrue();
        tuning.AppliedPower.Should().Be((0, 90f));
    }

    [Fact]
    public async Task ApplyNvApiPowerLimit_surfaces_tuning_failure_as_op_failed()
    {
        var tuning = new RecordingTuning(Result.Fail(ErrorCodes.HelperOpFailed, "NVIDIA NVAPI unavailable"));
        var exec = Build(tuning: tuning);

        var r = await exec.ExecuteAsync(new ApplyNvApiPowerLimitOp(0, 90), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // --- RISKY ADLX (AMD) tuning: Real now (T3), routed to the tuning seam ---

    [Fact]
    public async Task ApplyAdlxTuning_routes_to_adlx_actions_with_exact_args()
    {
        var adlx = new RecordingAdlx();
        var exec = Build(adlx: adlx);

        var r = await exec.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, -90), default);

        r.IsOk.Should().BeTrue();
        // It is NO LONGER GpuDeferred — the op reached the real write seam verbatim.
        adlx.Applied.Should().Be((0, AdlxTuningKind.Undervolt, -90));
    }

    [Fact]
    public async Task ApplyAdlxTuning_power_limit_routes_to_adlx_actions_with_exact_args()
    {
        var adlx = new RecordingAdlx();
        var exec = Build(adlx: adlx);

        var r = await exec.ExecuteAsync(new ApplyAdlxTuningOp(1, AdlxTuningKind.PowerLimit, 90), default);

        r.IsOk.Should().BeTrue();
        adlx.Applied.Should().Be((1, AdlxTuningKind.PowerLimit, 90));
    }

    [Fact]
    public async Task ApplyAdlxTuning_surfaces_adlx_failure_as_op_failed()
    {
        // Non-AMD box / unavailable ADLX / driver fault: the action fails clean; the
        // executor surfaces it (no false Ok, no swallow).
        var adlx = new RecordingAdlx(Result.Fail(ErrorCodes.HelperOpFailed, "AMD ADLX unavailable"));
        var exec = Build(adlx: adlx);

        var r = await exec.ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, -90), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    // --- Still-deferred GPU op: the fan-curve is cleanly REFUSED (R2), no action ---

    public static IEnumerable<object[]> DeferredGpuOps()
    {
        // ADLX tuning is NO LONGER deferred (T3) — only the fan-curve remains (T6).
        yield return new object[] { new ApplyGpuFanCurveOp(0, new[] { new FanCurvePointDto(80, 65) }) };
    }

    [Theory]
    [MemberData(nameof(DeferredGpuOps))]
    public async Task Deferred_gpu_ops_are_refused_clean_with_no_action(PrivilegedOp op)
    {
        var reg = new RecordingRegistry();
        var power = new RecordingPower();
        var restore = new RecordingRestore();
        var service = new RecordingService();
        var drs = new RecordingDrs();
        var tuning = new RecordingTuning();
        var adlx = new RecordingAdlx();
        var exec = Build(reg, power, restore, service, drs, tuning, adlx);

        var r = await exec.ExecuteAsync(op, default);

        r.IsOk.Should().BeFalse("the fan-curve remains deferred and must be refused");
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        // §22.3 honest unavailability: the message names the fan-curve and states plainly
        // that NO fan write happens — not a vague "(R2)" placeholder.
        r.Error.Value.Message.Should().Contain("fan-curve");
        r.Error.Value.Message.Should().Contain("no fan write");

        // No partial action anywhere — including no DRS / NVAPI / ADLX write.
        reg.Set.Should().BeNull();
        reg.Deleted.Should().BeNull();
        power.Applied.Should().BeNull();
        restore.Description.Should().BeNull();
        service.Controlled.Should().BeNull();
        service.PresentMonInstalled.Should().BeFalse();
        drs.Applied.Should().BeNull();
        tuning.AppliedOffset.Should().BeNull();
        tuning.AppliedPower.Should().BeNull();
        adlx.Applied.Should().BeNull();
    }

    [Fact]
    public async Task Null_op_fails_clean()
    {
        var r = await Build().ExecuteAsync(null!, default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }
}
