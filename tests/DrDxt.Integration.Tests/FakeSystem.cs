using System.Globalization;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// FakeSystemState — the in-memory model of "the machine" the H1 gate compares.
//
// The fake IElevatedClient WRITES into it (every privileged op mutates this and
// only this), and every read-seam reports the CURRENT value from it. So after an
// apply+revert the gate can compare a Snapshot() of the state against the baseline
// Snapshot() and assert they are byte-identical — the §20 money-back guarantee.
//
// It models exactly the surfaces the M2 tweaks touch:
//   * registry DWORDs (hive|path|name -> value)   [reflex, x3d cppc/gamebar]
//   * active power-plan GUID                        [sane power plan]
//   * Windows service states                        [x3d 3D V-Cache optimizer]
//   * NVIDIA DRS settings (id -> value)             [render queue cap, vrr/vsync]
//   * NV low-latency / pre-render / vsync / fps     [reflex / cap read seams]
//   * GPU clock offsets / power limit / fan curve   [undervolt / power / fan]
//   * ADLX tuning values (anti-lag/undervolt/power) [AMD render queue / tuning]
//
// Snapshot() is an ORDERED, deterministic string so a byte comparison is total.
// -----------------------------------------------------------------------
public sealed class FakeSystemState
{
    // Registry: keyed by "hive|path|name" → (TYPE, raw text). Absent key = not present.
    //
    // 19/09/2026 — la valeur etait un `uint`, et le client elevatoire faisait passer toute
    // ecriture par `ParseDword` : une chaine devenait le DWORD 0. En face, le lecteur rendait
    // `Ok(null)` pour toute chaine — « absente ». Un levier qui ecrit une chaine capturait donc
    // « absent » avant d'ecrire, fabriquait un inverse de SUPPRESSION, et la suppression rend
    // toujours la machine identique : le gate octet-pour-octet serait passe au vert sans avoir
    // jamais tenu une seule chaine. On garde donc le TYPE et le TEXTE tels qu'ecrits.
    private readonly Dictionary<string, (RegistryValueType Kind, string Value)> _registry =
        new(StringComparer.Ordinal);

    // Active power plan.
    public Guid ActivePowerPlan { get; set; } = KnownPowerPlans.Balanced;

    // L-CPU : le mode de boost secteur (PERFBOOSTMODE) du plan actif, et le défaut de sa personnalité.
    public uint ModeBoostSecteur { get; set; } = 2;
    // B1 (10/10/2026) : le registre 0x610 du banc : PL1 65 W, PL2 219 W (huitièmes de watt), activées, sans verrou.
    public ulong CpuPkgPowerLimit { get; set; } = (1752UL << 32) | (1UL << 47) | (1UL << 15) | 520UL;
    public uint ModeBoostDefaut { get; set; } = 2;
    // B3 (10/10/2026) : l'économie d'énergie de la carte sans fil (secteur) du plan actif : « moyenne » au départ.
    public uint EconomieSansFilSecteur { get; set; } = 2;

    // Service states.
    private readonly Dictionary<string, ServiceState> _services = new(StringComparer.Ordinal);

    // NVIDIA DRS settings (id → value).
    private readonly Dictionary<uint, uint> _drs = new();

    // NV driver latency reads.
    public uint NvLowLatencyMode { get; set; }
    public uint NvMaxPreRenderedFrames { get; set; } = 3;
    public uint NvVsyncMode { get; set; }
    public uint NvFrameRateLimit { get; set; }

    // Availability flags for the seams.
    public bool NvApiAvailable { get; set; }
    public bool AdlxAvailable { get; set; }

    // GPU tuning state.
    public int GpuCoreOffsetMhz { get; set; }
    public int GpuMemoryOffsetMhz { get; set; }
    public int GpuPowerLimitPct { get; set; } = 100;
    public FanCurve GpuFanCurve { get; set; } =
        new(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 60), new FanCurvePoint(90, 80) });

    // ADLX tuning state.
    public int AdlxAntiLag { get; set; }
    // AdlxUndervolt = the VOLTAGE the GetUndervoltValue probe reports (used only as an
    // .IsOk readability probe by ResolveTunableGpuIndex — its value never seeds a revert).
    public int AdlxUndervolt { get; set; }
    // AdlxMaxFreqMhz = the absolute GFX max clock the undervolt write actually moves. The
    // undervolt apply writes an absolute target here and its inverse op restores the absolute
    // original — so the T8 gate genuinely exercises absolute MaxFreq (a wrong-units / VoltMv
    // inverse would leave this at the wrong value and FAIL the byte-identical gate).
    public int AdlxMaxFreqMhz { get; set; } = 2900;
    public int AdlxPowerLimitPct { get; set; } = 100;

    // The display the VRR tweak targets.
    public DisplayInfo? PrimaryGameDisplay { get; set; }

    // L-ECRAN : le mode de l'écran, courant et enregistré dans le profil (ChangeDisplaySettingsEx).
    public ModeEcran? ModeEcranCourant { get; set; }
    public ModeEcran? ModeEcranEnregistre { get; set; }

    // --- registry ---
    private static string RegKey(RegistryHiveKind hive, string path, string name) =>
        $"{(int)hive}|{path}|{name}";

    /// <summary>La feuille lue comme DWORD. Une feuille d'un autre type rend `null` :
    /// « ce n'est pas un DWORD » est une reponse, la lire comme tel serait mentir.</summary>
    public uint? ReadRegistry(RegistryHiveKind hive, string path, string name) =>
        _registry.TryGetValue(RegKey(hive, path, name), out var v)
        && v.Kind == RegistryValueType.DWord
        && uint.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    /// <summary>La feuille lue comme REG_SZ (null si absente ou d'un autre type).</summary>
    public string? ReadRegistryText(RegistryHiveKind hive, string path, string name) =>
        _registry.TryGetValue(RegKey(hive, path, name), out var v)
        && v.Kind == RegistryValueType.String ? v.Value : null;

    /// <summary>La feuille lue comme REG_MULTI_SZ (null si absente ou d'un autre type).</summary>
    public string? ReadRegistryMultiText(RegistryHiveKind hive, string path, string name) =>
        _registry.TryGetValue(RegKey(hive, path, name), out var v)
        && v.Kind == RegistryValueType.MultiString ? v.Value : null;

    public void SetRegistry(RegistryHiveKind hive, string path, string name, uint value) =>
        SetRegistryValue(hive, path, name, RegistryValueType.DWord,
                         value.ToString(CultureInfo.InvariantCulture));

    public void SetRegistryValue(RegistryHiveKind hive, string path, string name,
                                 RegistryValueType kind, string value) =>
        _registry[RegKey(hive, path, name)] = (kind, value);

    public void DeleteRegistry(RegistryHiveKind hive, string path, string name) =>
        _registry.Remove(RegKey(hive, path, name));

    // --- services ---
    public ServiceState GetService(string name) =>
        _services.TryGetValue(name, out var s) ? s : ServiceState.NotInstalled;

    public void SetService(string name, ServiceState state) => _services[name] = state;

    // --- DRS ---
    public uint GetDrs(uint id) => _drs.TryGetValue(id, out var v) ? v : 0;
    public void SetDrs(uint id, uint value) => _drs[id] = value;

    // -----------------------------------------------------------------------
    // A deterministic, byte-comparable snapshot of the whole modeled state.
    // -----------------------------------------------------------------------
    public string Snapshot()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("registry:");
        foreach (var kv in _registry.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('[').Append(kv.Key).Append('=').Append(kv.Value.Kind).Append(':')
              .Append(kv.Value.Value).Append(']');

        sb.Append(";power=").Append(ActivePowerPlan.ToString("D")).Append(",boost=").Append(ModeBoostSecteur).Append(",pl=").Append(CpuPkgPowerLimit).Append(",wifi=").Append(EconomieSansFilSecteur);

        sb.Append(";services:");
        foreach (var kv in _services.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('[').Append(kv.Key).Append('=').Append(kv.Value).Append(']');

        sb.Append(";drs:");
        foreach (var kv in _drs.OrderBy(k => k.Key))
            sb.Append('[').Append(kv.Key).Append('=').Append(kv.Value).Append(']');

        sb.Append(";nv:").Append(NvLowLatencyMode).Append(',').Append(NvMaxPreRenderedFrames)
          .Append(',').Append(NvVsyncMode).Append(',').Append(NvFrameRateLimit);

        sb.Append(";gpu:").Append(GpuCoreOffsetMhz).Append(',').Append(GpuMemoryOffsetMhz)
          .Append(',').Append(GpuPowerLimitPct).Append(',').Append(FanCurveString(GpuFanCurve));

        sb.Append(";adlx:").Append(AdlxAntiLag).Append(',').Append(AdlxUndervolt)
          .Append(',').Append(AdlxMaxFreqMhz).Append(',').Append(AdlxPowerLimitPct);

        sb.Append(";ecran:").Append(ModeEcranCourant).Append(',').Append(ModeEcranEnregistre);

        return sb.ToString();
    }

    private static string FanCurveString(FanCurve c) =>
        string.Join("/", c.Points.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.TempC}:{p.DutyPct}")));
}

// -----------------------------------------------------------------------
// FakeElevatedClient — the only thing that mutates FakeSystemState. It applies each
// typed PrivilegedOp to the fake state (so the read-seams then report the new
// value) and records every write for assertions. It can be told to refuse a chosen
// op so a mid-batch failure can be simulated deterministically.
// -----------------------------------------------------------------------
public sealed class FakeElevatedClient : IElevatedClient
{
    private readonly FakeSystemState _state;
    private readonly Func<PrivilegedOp, bool>? _refuse;

    public List<PrivilegedOp> Writes { get; } = new();

    public FakeElevatedClient(FakeSystemState state, Func<PrivilegedOp, bool>? refuse = null)
    {
        _state = state;
        _refuse = refuse;
    }

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        if (_refuse is not null && _refuse(op))
            return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refused (test)"));

        switch (op)
        {
            case PingOp:
            case CreateRestorePointOp:
            case InstallPresentMonServiceOp:
                break; // no state change to model

            case SetRegistryValueOp r:
                // Le TYPE et le TEXTE tels qu'ecrits. Aplatir en DWORD faisait d'une chaine
                // un zero, et le gate comparait alors deux mensonges symetriques.
                _state.SetRegistryValue(r.Hive, r.Path, r.Name, r.Kind, r.Value);
                break;

            case DeleteRegistryValueOp d:
                _state.DeleteRegistry(d.Hive, d.Path, d.Name);
                break;

            case SetActivePowerPlanOp p:
                _state.ActivePowerPlan = p.PlanGuid;
                break;

            // Le banc ne modélise que le plan actif : une écriture visant un autre plan ne change rien ici.
            case SetProcessorBoostModeOp b when b.PlanGuid == _state.ActivePowerPlan:
                _state.ModeBoostSecteur = b.AcValue;
                break;

            case SetWirelessPowerSavingOp w when w.PlanGuid == _state.ActivePowerPlan:
                _state.EconomieSansFilSecteur = w.AcValue;
                break;

            // B1 (10/10/2026) : le registre de limite de puissance du processeur, tel quel.
            case SetCpuPackagePowerLimitOp c:
                _state.CpuPkgPowerLimit = c.Valeur;
                break;

            case ApplyNvApiClockOffsetOp c:
                _state.GpuCoreOffsetMhz = c.CoreOffsetMhz;
                _state.GpuMemoryOffsetMhz = c.MemoryOffsetMhz;
                break;

            case ApplyNvApiPowerLimitOp pl:
                _state.GpuPowerLimitPct = pl.PowerLimitPercent;
                break;

            case ApplyNvApiDrsSettingOp drs:
                _state.SetDrs(drs.SettingId, drs.Value);
                break;

            case ApplyAdlxTuningOp a:
                ApplyAdlx(a);
                break;

            case ApplyGpuFanCurveOp f:
                _state.GpuFanCurve = new FanCurve(
                    f.Points.Select(pt => new FanCurvePoint(pt.TempC, pt.DutyPct)).ToArray());
                break;

            case RunServiceControlOp s:
                ApplyService(s);
                break;

            default:
                return Task.FromResult(Result.Fail(ErrorCodes.NotAllowlisted, "unknown op (test)"));
        }

        Writes.Add(op);
        return Task.FromResult(Result.Ok());
    }

    private void ApplyAdlx(ApplyAdlxTuningOp a)
    {
        switch (a.Kind)
        {
            case AdlxTuningKind.AntiLag: _state.AdlxAntiLag = a.IntValue; break;
            // Undervolt carries an absolute MaxFreq (MHz) — write it to the MaxFreq state, the
            // surface the inverse op must restore. (not AdlxUndervolt, which is the voltage probe.)
            case AdlxTuningKind.Undervolt: _state.AdlxMaxFreqMhz = a.IntValue; break;
            case AdlxTuningKind.PowerLimit: _state.AdlxPowerLimitPct = a.IntValue; break;
        }
    }

    private void ApplyService(RunServiceControlOp s)
    {
        switch (s.Action)
        {
            case ServiceAction.Start: _state.SetService(s.ServiceName, ServiceState.Running); break;
            case ServiceAction.Stop: _state.SetService(s.ServiceName, ServiceState.Stopped); break;
            // Install / Query don't change the modeled running/stopped flag.
        }
    }

    private static uint ParseDword(string value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// -----------------------------------------------------------------------
// Read-seam fakes — every one reports the CURRENT value from the fake state, so a
// capture before an apply records the real "from" value and a faithful inverse op
// is produced. This is what makes the round-trip lossless.
// -----------------------------------------------------------------------
public sealed class FakeRegistryReader : IRegistryReader
{
    private readonly FakeSystemState _state;
    public FakeRegistryReader(FakeSystemState state) => _state = state;

    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) =>
        Result<uint?>.Ok(_state.ReadRegistry(hive, path, name));

    // 19/09/2026 — ces deux-la rendaient toujours `Ok(null)`, « absente ». Le commentaire disait
    // « ajouter un magasin de chaines le jour ou un levier en ecrit une » : deux en ecrivaient
    // deja (latency.vrrFlipModelOn, fixit.sane_pagefile). Un lecteur qui repond « absent » a
    // tout fabrique des inverses de SUPPRESSION, et une suppression restaure toujours.
    public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Ok(_state.ReadRegistryText(hive, path, name));

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Ok(_state.ReadRegistryMultiText(hive, path, name));
}

public sealed class FakeNvApi : INvApi
{
    private readonly FakeSystemState _state;
    public FakeNvApi(FakeSystemState state) => _state = state;

    public bool IsAvailable => _state.NvApiAvailable;
    public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(_state.NvLowLatencyMode);
    public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(_state.NvMaxPreRenderedFrames);
    public Result<uint> GetVsyncMode() => Result<uint>.Ok(_state.NvVsyncMode);
    public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(_state.NvFrameRateLimit);
    public Result<DrsOriginState> GetFrameRateLimitOrigin() =>
        Result<DrsOriginState>.Ok(DrsSettingOrigin.Describe(0, 0, 0));
    // heuristic: adaptive (0) default — no integration flow exercises Prefer-Max-Perf yet;
    // add a FakeSystemState field the day one does.
    public Result<uint> GetPreferredPstate() => Result<uint>.Ok(0);
}

public sealed class FakeAdlx : IAdlx
{
    private readonly FakeSystemState _state;
    public FakeAdlx(FakeSystemState state) => _state = state;

    public bool IsAvailable => _state.AdlxAvailable;
    public Result<int> GetAntiLagState() => Result<int>.Ok(_state.AdlxAntiLag);
}

public sealed class FakeDisplayInfo : IDisplayInfo
{
    private readonly FakeSystemState _state;
    public FakeDisplayInfo(FakeSystemState state) => _state = state;

    public Result<DisplayInfo> GetPrimaryGameDisplay() =>
        _state.PrimaryGameDisplay is { } d
            ? Result<DisplayInfo>.Ok(d)
            : Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "no display (fake)");

    public Result<IReadOnlyList<DisplayInfo>> GetAllDisplays() =>
        _state.PrimaryGameDisplay is { } d
            ? Result<IReadOnlyList<DisplayInfo>>.Ok(new[] { d })
            : Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable, "no display (fake)");
}

/// <summary>L-ECRAN : le mode de l'écran, posé dans l'état du banc. Un mode au-dessus de la fréquence max lue est refusé
/// à l'essai, comme Windows refuse un mode que l'écran n'énumère pas.</summary>
public sealed class FakeModeEcran(FakeSystemState state) : IModeEcran
{
    public ModeEcran? Courant(string peripherique) =>
        state.ModeEcranCourant is { } m && m.Peripherique == peripherique ? m : null;

    public int Tester(ModeEcran mode) => mode.Hz <= (state.PrimaryGameDisplay?.MaxRefreshHz ?? 0) ? 0 : -2;

    public int Ecrire(ModeEcran mode, bool enregistrer)
    {
        if (Tester(mode) != 0) return -2;
        state.ModeEcranCourant = mode;
        if (enregistrer) state.ModeEcranEnregistre = mode;
        return 0;
    }
}

public sealed class ConfirmationQuiGarde : IConfirmationEcran
{
    public Task<bool> GarderAsync(int hz, TimeSpan delai) => Task.FromResult(true);
}

public sealed class FakeNvApiGpuTuning : INvApiGpuTuning
{
    private readonly FakeSystemState _state;
    public FakeNvApiGpuTuning(FakeSystemState state) => _state = state;

    public bool IsAvailable => _state.NvApiAvailable;
    public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => Result<int>.Ok(_state.GpuCoreOffsetMhz);
    public Result<int> GetMemoryClockOffsetMhz(int gpuIndex) => Result<int>.Ok(_state.GpuMemoryOffsetMhz);
    public Result<int> GetPowerLimitPct(int gpuIndex) => Result<int>.Ok(_state.GpuPowerLimitPct);
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Result<FanCurve>.Ok(_state.GpuFanCurve);
}

public sealed class FakeAdlxGpuTuning : IAdlxGpuTuning
{
    private readonly FakeSystemState _state;
    public FakeAdlxGpuTuning(FakeSystemState state) => _state = state;

    public bool IsAvailable => _state.AdlxAvailable;
    // The modeled machine has a single tunable GPU at index 0 when ADLX is available.
    public Result<int> ResolveTunableGpuIndex() =>
        _state.AdlxAvailable
            ? Result<int>.Ok(0)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available (fake).");
    public Result<int> GetUndervoltValue(int gpuIndex) => Result<int>.Ok(_state.AdlxUndervolt);
    public Result<int> GetGfxMaxFreqMhz(int gpuIndex) => Result<int>.Ok(_state.AdlxMaxFreqMhz);
    public Result<int> GetPowerLimitPct(int gpuIndex) => Result<int>.Ok(_state.AdlxPowerLimitPct);
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Result<FanCurve>.Ok(_state.GpuFanCurve);
}

public sealed class FakePowerPlanReader : IPowerPlanReader
{
    private readonly FakeSystemState _state;
    public FakePowerPlanReader(FakeSystemState state) => _state = state;

    public Result<Guid> GetActivePowerPlan() => Result<Guid>.Ok(_state.ActivePowerPlan);

    public Result<ModeDeBoostLu> LireModeDeBoost(Guid plan) => plan == _state.ActivePowerPlan
        ? Result<ModeDeBoostLu>.Ok(new ModeDeBoostLu(plan, _state.ModeBoostSecteur, _state.ModeBoostDefaut))
        : Result<ModeDeBoostLu>.Fail(ErrorCodes.TweakSdkUnavailable, "plan non modélisé");

    public Result<EconomieSansFilLue> LireEconomieSansFil(Guid plan) => plan == _state.ActivePowerPlan
        ? Result<EconomieSansFilLue>.Ok(new EconomieSansFilLue(plan, _state.EconomieSansFilSecteur))
        : Result<EconomieSansFilLue>.Fail(ErrorCodes.TweakSdkUnavailable, "plan non modélisé");
}

/// <summary>B1 (10/10/2026) : les registres de limite de puissance du banc (unité = huitièmes de watt, base 65 W).</summary>
public sealed class FakeCpuPowerLimitReader : ICpuPowerLimitReader
{
    private readonly FakeSystemState _state;
    public FakeCpuPowerLimitReader(FakeSystemState state) => _state = state;

    public Result<LectureLimiteCpu> LireRegistres()
    {
        var registres = new RegistresCpuLus(0x3, _state.CpuPkgPowerLimit, 520UL);
        var lu = RegistreDeLimiteCpu.Decoder(0x3, _state.CpuPkgPowerLimit, 520UL);
        return lu is null
            ? Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "registres vides")
            : Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(registres, lu with { CopieMmioPl1W = 0 }));
    }
}

public sealed class FakeServiceStateReader : IServiceStateReader
{
    private readonly FakeSystemState _state;
    public FakeServiceStateReader(FakeSystemState state) => _state = state;

    public Result<ServiceState> GetServiceState(string serviceName) =>
        Result<ServiceState>.Ok(_state.GetService(serviceName));
}

// A stability tester that always reports stable — so a GPU undervolt is kept and
// then auto-reverted by the engine (rather than self-reverting), exercising the
// engine's revert path on a GPU op.
public sealed class StableStabilityTester : IStabilityTester
{
    public Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct) =>
        Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable));
}
