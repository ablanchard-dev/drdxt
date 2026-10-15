using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiTuningActions : les vraies écritures de réglage NVAPI risquées (la frontière du m2smoke).
//
// Reprend l'unique frontière NVAPI native vérifiée : NvApiNative dans DrDxt.GpuNative (la recette
// privée rétro-ingéniée SetPstates20 / ClientPowerPoliciesSetStatus, validée en réel sur une
// RTX 4060). Le helper n'a aucune seconde copie des ids de fonction ni de la recette GET puis SET
// (le piège de divergence écarté) : il orchestre seulement sur INvApiNative, exactement comme
// ADLXTuningActions sur IAdlxNative.
//
// Règles d'or (imposées dans NvApiNative) :
//   #1 mot de version = Marshal.SizeOf(struct) | (1 << 16) à l'exécution : une taille incorrecte
//      devient l'IncompatibleStructVersion sûr du pilote, jamais une écriture.
//   #2 GET puis SET : lire toute la struct actuelle, ne modifier que le seul champ visé sur place,
//      la réécrire ; jamais une struct d'écriture construite à zéro.
//   #3 statut 0 = Ok ; tout non nul → la frontière rend false → Result.Fail ici.
//
// Défense en profondeur (cette couche) : les valeurs d'écriture hors règle sont refusées ici par
// les garde-fous de plage de NvApiTuningMath (qui reprennent GpuSafeBounds de l'App et rejettent
// aussi int.MinValue et NaN) avant d'atteindre la frontière native : l'écriture GPU élevée impose
// elle-même la plage de sécurité et ne fait jamais confiance à son appelant pour les bornes, même
// sur un appel direct (hors IPC) d'un outil de smoke test qui contourne OpAllowlist. Hors plage :
// rejeté, pas borné.
//
// Chaque méthode rend un Result et ne lève jamais à travers l'IPC. Sur une machine AMD / sans
// NVIDIA, ou si un id de fonction privée n'est pas exporté par le pilote installé, la frontière
// rend false et l'appel échoue proprement (HELPER.OP_FAILED).
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
    /// Une écriture native qui rend true veut dire que le pilote a accepté l'appel, pas que la valeur
    /// est en place. Relevé sur une vraie RTX 4060 : une demande de limite de puissance à 110 % a rendu
    /// true et la carte est restée à 100 % ; elle n'a pas de marge et le pilote a borné en silence.
    /// L'annoncer comme appliqué dirait à un client qu'on a relevé sa limite alors que rien n'a bougé,
    /// et enregistrerait une valeur de retour que le GPU n'a jamais tenue.
    ///
    /// Chaque écriture relit donc et compare. La comparaison est exacte exprès : un pilote qui
    /// arrondit une demande apparaîtrait ici comme un désaccord, ce qui renseigne sur ce pilote,
    /// pas un défaut à masquer par une tolérance inventée.
    ///
    /// Une relecture qui échoue n'est pas un succès non plus : « appliqué mais non confirmé » est
    /// exactement l'affirmation que cette méthode existe pour empêcher.
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
