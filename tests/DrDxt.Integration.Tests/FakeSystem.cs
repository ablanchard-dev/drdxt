using System.Globalization;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// FakeSystemState : le modèle en mémoire de « la machine » que la porte de retour arrière compare.
//
// Le faux IElevatedClient écrit dedans (chaque op privilégiée mute ceci et rien d'autre),
// et chaque couture de lecture rapporte la valeur courante qui s'y trouve. Après un
// apply + revert, la porte peut donc comparer un Snapshot() de l'état au Snapshot() de
// départ et vérifier qu'ils sont identiques à l'octet près : la promesse qu'un retour arrière rend la machine.
//
// Il modélise exactement les surfaces que les leviers touchent :
//   * DWORD de registre (ruche|chemin|nom → valeur)   [reflex, x3d cppc/gamebar]
//   * GUID du plan d'alimentation actif                 [plan d'alimentation sain]
//   * états des services Windows                        [optimiseur 3D V-Cache x3d]
//   * réglages DRS NVIDIA (id → valeur)                 [plafond de file de rendu, vrr/vsync]
//   * latence basse NV / pré-rendu / vsync / fps        [coutures de lecture reflex / plafond]
//   * décalages d'horloge GPU / limite de puissance / courbe [undervolt / puissance / ventilation]
//   * valeurs de réglage ADLX (anti-lag/undervolt/puissance) [file de rendu AMD / réglage]
//
// Snapshot() est une chaîne ordonnée et déterministe, pour qu'une comparaison d'octets soit totale.
// -----------------------------------------------------------------------
public sealed class FakeSystemState
{
    // Registre : indexé par « ruche|chemin|nom » → (type, texte brut). Clé absente = valeur absente.
    //
    // La valeur etait un `uint`, et le client elevatoire faisait passer toute
    // ecriture par `ParseDword` : une chaine devenait le DWORD 0. En face, le lecteur rendait
    // `Ok(null)` pour toute chaine — « absente ». Un levier qui ecrit une chaine capturait donc
    // « absent » avant d'ecrire, fabriquait un inverse de SUPPRESSION, et la suppression rend
    // toujours la machine identique : le gate octet-pour-octet serait passe au vert sans avoir
    // jamais tenu une seule chaine. On garde donc le TYPE et le TEXTE tels qu'ecrits.
    private readonly Dictionary<string, (RegistryValueType Kind, string Value)> _registry =
        new(StringComparer.Ordinal);

    // Plan d'alimentation actif.
    public Guid ActivePowerPlan { get; set; } = KnownPowerPlans.Balanced;

    // Le mode de boost secteur (PERFBOOSTMODE) du plan actif, et le défaut de sa personnalité.
    public uint ModeBoostSecteur { get; set; } = 2;
    // Le registre 0x610 du banc : PL1 65 W, PL2 219 W (huitièmes de watt), activées, sans verrou.
    public ulong CpuPkgPowerLimit { get; set; } = (1752UL << 32) | (1UL << 47) | (1UL << 15) | 520UL;
    public uint ModeBoostDefaut { get; set; } = 2;
    // L'économie d'énergie de la carte sans fil (secteur) du plan actif : « moyenne » au départ.
    public uint EconomieSansFilSecteur { get; set; } = 2;

    // États des services.
    private readonly Dictionary<string, ServiceState> _services = new(StringComparer.Ordinal);

    // Réglages DRS NVIDIA (id → valeur).
    private readonly Dictionary<uint, uint> _drs = new();

    // Lectures de latence du pilote NV.
    public uint NvLowLatencyMode { get; set; }
    public uint NvMaxPreRenderedFrames { get; set; } = 3;
    public uint NvVsyncMode { get; set; }
    public uint NvFrameRateLimit { get; set; }

    // Drapeaux de disponibilité des coutures.
    public bool NvApiAvailable { get; set; }
    public bool AdlxAvailable { get; set; }

    // État de réglage GPU.
    public int GpuCoreOffsetMhz { get; set; }
    public int GpuMemoryOffsetMhz { get; set; }
    public int GpuPowerLimitPct { get; set; } = 100;
    public FanCurve GpuFanCurve { get; set; } =
        new(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 60), new FanCurvePoint(90, 80) });

    // État de réglage ADLX.
    public int AdlxAntiLag { get; set; }
    // AdlxUndervolt = la tension que rapporte la sonde GetUndervoltValue (utilisée seulement comme
    // sonde de lisibilité .IsOk par ResolveTunableGpuIndex ; sa valeur n'alimente jamais un retour arrière).
    public int AdlxUndervolt { get; set; }
    // AdlxMaxFreqMhz = l'horloge max GFX absolue que l'écriture d'undervolt déplace vraiment.
    // L'application de l'undervolt y écrit une cible absolue et son op inverse rend l'origine
    // absolue : la porte AMD exerce donc vraiment la MaxFreq absolue (un inverse en mauvaises
    // unités ou en VoltMv laisserait ici une mauvaise valeur et ferait échouer la porte à l'octet près).
    public int AdlxMaxFreqMhz { get; set; } = 2900;
    public int AdlxPowerLimitPct { get; set; } = 100;

    // L'écran que vise le levier VRR.
    public DisplayInfo? PrimaryGameDisplay { get; set; }

    // Le mode de l'écran, courant et enregistré dans le profil (ChangeDisplaySettingsEx).
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
    /// <summary>Le réglage DRS, ou null quand il n'est posé nulle part (le pilote applique alors son défaut).</summary>
    public uint? LireDrs(uint id) => _drs.TryGetValue(id, out var v) ? v : null;
    public void SetDrs(uint id, uint value) => _drs[id] = value;

    // -----------------------------------------------------------------------
    // Instantané déterministe et comparable à l'octet près de tout l'état modélisé.
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
// FakeElevatedClient : la seule chose qui mute FakeSystemState. Il applique chaque
// PrivilegedOp typée à l'état factice (les coutures de lecture rapportent alors la nouvelle
// valeur) et enregistre chaque écriture pour les assertions. On peut lui dire de refuser une
// op choisie, pour simuler de façon déterministe un échec en plein lot.
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
                break; // aucun changement d'état à modéliser

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

            // Le registre de limite de puissance du processeur, tel quel.
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

            // Comme Win32ServiceActions : démarrer ou arrêter un service absent lève, et l'op échoue.
            case RunServiceControlOp { Action: ServiceAction.Start or ServiceAction.Stop } s
                when _state.GetService(s.ServiceName) == ServiceState.NotInstalled:
                return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "service not installed (test)"));

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
            // Undervolt porte une MaxFreq absolue (MHz) : l'écrire dans l'état MaxFreq, la surface
            // que l'op inverse doit rendre (pas AdlxUndervolt, qui est la sonde de tension).
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
            // Install / Query ne changent pas le drapeau modélisé en route/arrêté.
        }
    }

    private static uint ParseDword(string value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// -----------------------------------------------------------------------
// Faux des coutures de lecture : chacune rapporte la valeur courante de l'état factice, pour
// qu'une capture avant application enregistre la vraie valeur de départ et qu'une op inverse
// fidèle soit produite. C'est ce qui rend l'aller-retour sans perte.
// -----------------------------------------------------------------------
public sealed class FakeRegistryReader : IRegistryReader
{
    private readonly FakeSystemState _state;
    public FakeRegistryReader(FakeSystemState state) => _state = state;

    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) =>
        Result<uint?>.Ok(_state.ReadRegistry(hive, path, name));

    // Un vrai magasin de chaines : des leviers en ecrivent (latency.vrrFlipModelOn, fixit.sane_pagefile).
    // Un lecteur qui repond « absent » a tout fabriquerait des inverses de SUPPRESSION, et une
    // suppression restaure toujours.
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
    // Comme le vrai lecteur (NvApiNative.TryGetDrsGlobalSetting) : un réglage posé nulle part échoue
    // (NVAPI_SETTING_NOT_FOUND), il ne rend pas une valeur que personne n'a écrite.
    public Result<uint> GetMaxPreRenderedFrames() => Drs(0x007ba09e);
    public Result<uint> GetVsyncMode() => Result<uint>.Ok(_state.NvVsyncMode);
    public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(_state.NvFrameRateLimit);
    public Result<DrsOriginState> GetFrameRateLimitOrigin() =>
        Result<DrsOriginState>.Ok(DrsSettingOrigin.Describe(0, 0, 0));
    public Result<uint> GetPreferredPstate() => Drs(0x1057EB71);

    private Result<uint> Drs(uint id) => _state.LireDrs(id) is { } v
        ? Result<uint>.Ok(v)
        : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "DRS setting not set (driver default).");
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

/// <summary>Le mode de l'écran, posé dans l'état du banc. Un mode au-dessus de la fréquence max lue est refusé
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
    // La machine modélisée a une seule carte réglable à l'index 0 quand ADLX est disponible.
    public Result<int> ResolveTunableGpuIndex() =>
        _state.AdlxAvailable
            ? Result<int>.Ok(0)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available (fake).");
    public Result<int> GetUndervoltValue(int gpuIndex) => Result<int>.Ok(_state.AdlxUndervolt);
    public Result<int> GetGfxMaxFreqMhz(int gpuIndex) => Result<int>.Ok(_state.AdlxMaxFreqMhz);
    public bool? EstALUsine(int gpuIndex) => true; // la carte modélisée part de l'usine
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

/// <summary>Les registres de limite de puissance du banc (unité = huitièmes de watt, base 65 W), avec des parties relevées
/// sous le seuil de chaleur (80 °C) : sans température de partie, la montée n'est jamais proposée.</summary>
public sealed class FakeCpuPowerLimitReader : ICpuPowerLimitReader
{
    private readonly FakeSystemState _state;
    private readonly double? _plafondThermique;
    /// <param name="plafondThermique">Le plafond que la dernière partie demande (levier `fixit.cpu_power_cap`).</param>
    public FakeCpuPowerLimitReader(FakeSystemState state, double? plafondThermique = null)
    {
        _state = state;
        _plafondThermique = plafondThermique;
    }

    public Result<LectureLimiteCpu> LireRegistres()
    {
        var registres = new RegistresCpuLus(0x3, _state.CpuPkgPowerLimit, 520UL);
        var lu = RegistreDeLimiteCpu.Decoder(0x3, _state.CpuPkgPowerLimit, 520UL);
        return lu is null
            ? Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "registres vides")
            : Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(registres, lu with { CopieMmioPl1W = 0, PlafondThermiqueW = _plafondThermique, ChaleurEnPartieMaxC = 80 }));
    }
}

public sealed class FakeServiceStateReader : IServiceStateReader
{
    private readonly FakeSystemState _state;
    public FakeServiceStateReader(FakeSystemState state) => _state = state;

    public Result<ServiceState> GetServiceState(string serviceName) =>
        Result<ServiceState>.Ok(_state.GetService(serviceName));
}

// Un testeur de stabilité qui dit toujours stable : un undervolt GPU est donc gardé puis
// annulé automatiquement par le moteur (au lieu de s'annuler lui-même), ce qui exerce
// le chemin de retour arrière du moteur sur une op GPU.
public sealed class StableStabilityTester : IStabilityTester
{
    public Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct) =>
        Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable));
}
