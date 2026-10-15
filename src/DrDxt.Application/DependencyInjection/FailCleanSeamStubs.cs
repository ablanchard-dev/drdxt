using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.DependencyInjection;

// -----------------------------------------------------------------------
// Stubs des coutures SDK qui échouent proprement.
//
// Les vraies implémentations P/Invoke (NVAPI / ADLX / registre / alimentation / point de
// restauration / affichage) vivent dans Detection / Infrastructure. Sans elles, le conteneur
// DI a besoin de quelque chose à injecter pour résoudre le graphe, et la règle d'or est
// d'échouer fermé, jamais ouvert : chaque couture ici dit « indisponible », un levier passe
// proprement (TWEAK.SDK_UNAVAILABLE) et rien de privilégié n'arrive par accident.
//
// En particulier, StubElevatedClient refuse chaque op (il n'écrit rien de privilégié), et
// StubRestorePointService dit la restauration désactivée : le moteur bloque avant toute
// pose. C'est le défaut sûr : une version à moitié câblée peut lire et planifier, pas poser.
//
// Ils sont enregistrés par TryAdd* : la racine de composition de l'App peut remplacer
// chacun par la vraie implémentation sans toucher ce fichier.
// -----------------------------------------------------------------------

/// <summary>IElevatedClient qui échoue proprement : refuse chaque op (aucune écriture privilégiée).
/// Le vrai client en IPC signé est branché dans l'hôte App.</summary>
internal sealed class StubElevatedClient : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
        Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed,
            "Elevated helper is not wired in this build (fail-clean stub)."));
}

/// <summary>Couture de point de restauration qui échoue proprement : dit la restauration
/// désactivée, pour que le SafetyManager et le moteur bloquent avant toute pose (jamais sans vrai point).</summary>
internal sealed class StubRestorePointService : IRestorePointService
{
    public bool IsEnabled() => false;
    public bool TryEnable() => false;
    public DateTime? DernierPointDepuis(DateTime depuisUtc) => null;
}

/// <summary>Demande de consentement qui échoue proprement : refuse (pas d'UI dans un graphe
/// sans écran). L'hôte App la remplace par une vraie boîte de dialogue.</summary>
internal sealed class StubConsentPrompt : IConsentPrompt
{
    public bool AskEnableRestore() => false;
}

/// <summary>Couture de latence NVAPI qui échoue proprement : indisponible.</summary>
internal sealed class StubNvApi : INvApi
{
    public bool IsAvailable => false;
    public Result<uint> GetLowLatencyMode() => Unavailable<uint>();
    public Result<uint> GetMaxPreRenderedFrames() => Unavailable<uint>();
    public Result<uint> GetVsyncMode() => Unavailable<uint>();
    public Result<uint> GetFrameRateLimit() => Unavailable<uint>();
    public Result<DrsOriginState> GetFrameRateLimitOrigin() => Unavailable<DrsOriginState>();
    public Result<uint> GetPreferredPstate() => Unavailable<uint>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI not available (stub).");
}

/// <summary>Couture de latence ADLX qui échoue proprement : indisponible.</summary>
internal sealed class StubAdlx : IAdlx
{
    public bool IsAvailable => false;
    public Result<int> GetAntiLagState() =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available (stub).");
}

/// <summary>Couture d'affichage qui échoue proprement : ne sait pas trouver l'écran du jeu.</summary>
internal sealed class StubDisplayInfo : IDisplayInfo
{
    public Result<DisplayInfo> GetPrimaryGameDisplay() =>
        Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "Display info not available (stub).");

    public Result<IReadOnlyList<DisplayInfo>> GetAllDisplays() =>
        Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable,
            "Display info not available (stub).");
}

/// <summary>Couture de lecture du registre qui échoue proprement : erreur de lecture (jamais
/// « absent », qui serait une vraie valeur relevée).</summary>
internal sealed class StubRegistryReader : IRegistryReader
{
    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) =>
        Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");

    public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) =>
        Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read not available (stub).");
}

/// <summary>Couture de réglage GPU NVAPI qui échoue proprement : indisponible.</summary>
internal sealed class StubNvApiGpuTuning : INvApiGpuTuning
{
    public bool IsAvailable => false;
    public Result<int> GetCoreClockOffsetMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetMemoryClockOffsetMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetPowerLimitPct(int gpuIndex) => Unavailable<int>();
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Unavailable<FanCurve>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI GPU tuning not available (stub).");
}

/// <summary>Couture de réglage GPU ADLX qui échoue proprement : indisponible.</summary>
internal sealed class StubAdlxGpuTuning : IAdlxGpuTuning
{
    public bool IsAvailable => false;
    public Result<int> ResolveTunableGpuIndex() => Unavailable<int>();
    public Result<int> GetUndervoltValue(int gpuIndex) => Unavailable<int>();
    public Result<int> GetGfxMaxFreqMhz(int gpuIndex) => Unavailable<int>();
    public Result<int> GetPowerLimitPct(int gpuIndex) => Unavailable<int>();
    public Result<FanCurve> GetFanCurve(int gpuIndex) => Unavailable<FanCurve>();

    private static Result<T> Unavailable<T>() =>
        Result<T>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX GPU tuning not available (stub).");
}

/// <summary>Sonde de charge GPU qui échoue proprement : la vraie charge D3D11 n'est pas
/// branchée ici, elle rend CouldNotRun → GpuStabilityTester rend Inconclusive → une
/// sous-tension n'est jamais gardée (elle s'annule). Remplacée par la vraie sonde D3D11.</summary>
internal sealed class StubGpuLoadProbe : IGpuLoadProbe
{
    public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct) =>
        Task.FromResult(Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun));
}

/// <summary>Couture de lecture du plan d'alimentation qui échoue proprement : indisponible.</summary>
internal sealed class StubPowerPlanReader : IPowerPlanReader
{
    public Result<Guid> GetActivePowerPlan() =>
        Result<Guid>.Fail(ErrorCodes.TweakSdkUnavailable, "Power-plan read not available (stub).");
}

/// <summary>Couture de lecture de la limite de puissance CPU qui échoue proprement : indisponible (l'App branche le lecteur PawnIO).</summary>
internal sealed class StubCpuPowerLimitReader : ICpuPowerLimitReader { }

/// <summary>Couture de lecture de l'état d'un service qui échoue proprement : indisponible.</summary>
internal sealed class StubServiceStateReader : IServiceStateReader
{
    public Result<ServiceState> GetServiceState(string serviceName) =>
        Result<ServiceState>.Fail(ErrorCodes.TweakSdkUnavailable, "Service-state read not available (stub).");
}

/// <summary>Fail-clean : le mode de l'écran n'est pas lu, et rien n'est posé.</summary>
internal sealed class StubModeEcran : IModeEcran
{
    private const int DispChangeFailed = -1;
    public ModeEcran? Courant(string peripherique) => null;
    public int Tester(ModeEcran mode) => DispChangeFailed;
    public int Ecrire(ModeEcran mode, bool enregistrer) => DispChangeFailed;
}

/// <summary>Fail-closed : sans fenêtre pour demander, le nouveau mode n'est pas gardé.</summary>
internal sealed class StubConfirmationEcran : IConfirmationEcran
{
    public Task<bool> GarderAsync(int hz, TimeSpan delai) => Task.FromResult(false);
}
