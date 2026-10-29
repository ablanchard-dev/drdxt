using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiTuningActions : les vraies écritures de réglage NVAPI risquées (la frontière du m2smoke).
//
// Orchestre sur INvApiNative, comme ADLXTuningActions sur IAdlxNative : la recette (SetPstates20,
// ClientPowerPoliciesSetStatus, validée sur une RTX 4060) et ses règles d'or ne vivent que dans NvApiNative.
//
// Défense en profondeur : NvApiTuningMath (GpuSafeBounds, plus int.MinValue et NaN) refuse ici toute valeur hors règle
// avant la frontière native, même sur un appel direct d'un outil de smoke test qui contourne OpAllowlist. Hors plage :
// rejeté, pas borné. Sans NVIDIA ou sans id exporté par le pilote : échec propre (HELPER.OP_FAILED), jamais d'exception.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvApiTuningActions : INvApiTuningActions
{
    private const string Unavailable = "NVIDIA NVAPI unavailable";

    private readonly INvApiNative _native;

    public NvApiTuningActions() : this(new NvApiNative()) { }

    internal NvApiTuningActions(INvApiNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    // --- lectures (capture de l'original pour un retour fidèle) ---

    public Result<int> GetMemoryClockOffsetMhz(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            return _native.TryReadMemoryOffsetMhz(gpuIndex, out var mhz)
                ? Result<int>.Ok(mhz)
                : Result<int>.Fail(ErrorCodes.HelperOpFailed, "Memory clock offset not readable.");
        }
        catch (Exception ex)
        {
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, ex.Message);
        }
    }

    public Result<int> GetCoreClockOffsetMhz(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            return _native.TryReadCoreOffsetMhz(gpuIndex, out var mhz)
                ? Result<int>.Ok(mhz)
                : Result<int>.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI core-clock offset read failed (no NVIDIA GPU at index, or driver fault).");
        }
        catch (Exception ex)
        {
            // La frontière native avale déjà les pannes (rend false) ; garde de dernier recours
            // pour qu'aucune exception ne traverse l'IPC.
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, $"NVAPI read failed: {ex.GetType().Name}.");
        }
    }

    public Result<int> GetPowerLimitPercent(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            return _native.TryReadPowerLimitPct(gpuIndex, out var pct)
                ? Result<int>.Ok(pct)
                : Result<int>.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI power-limit read failed (no NVIDIA GPU at index, or driver fault).");
        }
        catch (Exception ex)
        {
            return Result<int>.Fail(ErrorCodes.HelperOpFailed, $"NVAPI read failed: {ex.GetType().Name}.");
        }
    }

    // --- écritures (GET puis SET via la feuille ; plage de règle revérifiée ici) ---

    public Result SetCoreClockOffset(int gpuIndex, int offsetMHz)
    {
        // Plage de règle imposée aussi dans l'exécuteur (sans Math.Abs → int.MinValue est sûr) :
        // l'écriture élevée ne fait jamais confiance à son appelant pour les bornes, même sur un appel
        // direct (hors IPC) d'un outil de smoke test qui contourne OpAllowlist. Hors plage : rejeté, pas borné.
        if (!NvApiTuningMath.IsCoreOffsetWithinHardRail(offsetMHz))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"Refused: core-clock offset {offsetMHz} MHz is outside the GPU safe-bounds policy band " +
                $"[{NvApiTuningMath.MinCoreOffsetMhz}, {NvApiTuningMath.MaxCoreOffsetMhz}] MHz.");
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            if (!_native.TryReadCoreOffsetMhz(gpuIndex, out var origine))
                return Result.Fail(ErrorCodes.HelperOpFailed, "NVAPI core-clock offset could not be read before the write; nothing written.");
            if (!_native.TrySetCoreClockOffset(gpuIndex, offsetMHz))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI core-clock write failed (clock not editable, private id absent, or driver fault).");

            var confirme = Confirm(offsetMHz, _native.TryReadCoreOffsetMhz(gpuIndex, out var actualMhz),
                actualMhz, "core-clock offset", "MHz");
            return confirme.IsOk ? confirme : RendreOrigine(confirme, origine, "core-clock offset", "MHz",
                () => _native.TrySetCoreClockOffset(gpuIndex, origine),
                () => (_native.TryReadCoreOffsetMhz(gpuIndex, out var v), v));
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"NVAPI write failed: {ex.GetType().Name}.");
        }
    }

    public Result SetPowerLimitPercent(int gpuIndex, float pct)
    {
        // Garde-fou strict (rejette NaN / Infinity, qui passent à travers des comparaisons naïves).
        if (!NvApiTuningMath.IsPowerPercentWithinHardRail(pct))
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"Refused: power-limit {pct}% is outside the hard [{NvApiTuningMath.MinPowerPercent}, {NvApiTuningMath.MaxPowerPercent}]% rail.");
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "Invalid GPU index.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.HelperOpFailed, Unavailable);
        try
        {
            if (!_native.TryReadPowerLimitPct(gpuIndex, out var origine))
                return Result.Fail(ErrorCodes.HelperOpFailed, "NVAPI power limit could not be read before the write; nothing written.");
            if (!_native.TrySetPowerLimitPercent(gpuIndex, pct))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    "NVAPI power-limit write failed (invalid driver bounds or driver fault).");

            var confirme = Confirm((int)MathF.Round(pct), _native.TryReadPowerLimitPct(gpuIndex, out var actualPct),
                actualPct, "power limit", "%");
            return confirme.IsOk ? confirme : RendreOrigine(confirme, origine, "power limit", "%",
                () => _native.TrySetPowerLimitPercent(gpuIndex, origine),
                () => (_native.TryReadPowerLimitPct(gpuIndex, out var v), v));
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"NVAPI write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Une écriture non confirmée a pu laisser autre chose que l'origine : le pilote ramène une demande hors de ses bornes
    /// à sa borne et l'écrit (100 % demandé 60, minimum 70 : 70 posé). L'App ne garde rien d'un échec, donc l'origine est
    /// reposée et relue ici ; si elle ne revient pas, c'est dit avec la valeur laissée.
    /// </summary>
    private static Result RendreOrigine(Result echec, int origine, string what, string unit,
        Func<bool> reposer, Func<(bool Ok, int Valeur)> relire)
    {
        var avant = relire();
        if (avant.Ok && avant.Valeur == origine) return echec;
        reposer();
        var apres = relire();
        if (apres.Ok && apres.Valeur == origine) return echec;
        return Result.Fail(ErrorCodes.TweakPartiallyApplied,
            $"NVAPI {what} was not applied and could not be restored: the driver holds " +
            $"{(apres.Ok ? apres.Valeur + unit : "an unreadable value")} (was {origine}{unit}); reset it in the NVIDIA app or reboot.");
    }

    /// <summary>
    /// Un true natif dit que le pilote a accepté l'appel, pas que la valeur est en place : sur une RTX 4060, 110 % de
    /// limite a rendu true et la carte est restée à 100 %, bornée en silence. Chaque écriture relit et compare exactement
    /// (un arrondi du pilote est un désaccord qui renseigne, pas un défaut à masquer). Une relecture ratée n'est pas un succès.
    /// </summary>
    private static Result Confirm(int requested, bool readOk, int actual, string what, string unit)
    {
        if (!readOk)
            return Result.Fail(ErrorCodes.HelperOpFailed,
                $"NVAPI {what} write was accepted but could not be read back, so it is not confirmed.");

        return actual == requested
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed,
                $"NVAPI {what} was not applied: asked {requested}{unit}, the driver holds {actual}{unit} " +
                "(it accepted the call and clamped).");
    }
}
