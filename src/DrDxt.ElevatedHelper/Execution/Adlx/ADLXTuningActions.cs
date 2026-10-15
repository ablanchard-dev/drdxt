using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// ADLXTuningActions : les vraies écritures de réglage AMD ADLX risquées (la frontière du
// m2smoke), le miroir AMD de NvApiTuningActions.
//
// Reprend l'unique frontière ADLX native vérifiée : AdlxNative dans DrDxt.GpuNative (validée en
// réel sur RX 9070 XT / RDNA4, emplacements vérifiés contre les en-têtes du SDK AMD ADLX). Le
// helper n'a aucune seconde copie des emplacements de vtable (le piège de divergence écarté) :
// il orchestre seulement GET puis SET sur IAdlxNative.
//
// Règles d'or, reprises du chemin NVAPI :
//   #1 GET puis SET : AdlxNative lit tout l'état actuel, valide la cible contre la plage annoncée
//      par le pilote lui-même et n'écrit que le seul bouton visé. On fait d'abord un GET explicite
//      (TryReadGfx) pour lire la tension actuelle (renvoyée inchangée) et la plage matérielle
//      MaxFreq du pilote, utilisée par la vérification de défense en profondeur plus bas.
//   #2 Bornes de défense en profondeur (deux plages distinctes, voulues) :
//        • La plage de règle prudente [-200,+100] MHz est une plage de décalage et vit dans la
//          couche App (GpuSafeBounds), imposée avant que l'op atteigne l'IPC. L'op d'undervolt
//          porte une MaxFreq absolue (MHz), pas un décalage : une plage de décalage n'a pas de
//          sens ici et n'est pas revérifiée dans le helper.
//        • Le helper valide plutôt la cible absolue contre la plage matérielle annoncée par le
//          pilote (AdlxGfxReads.MaxFreqRange) et rejette (échec propre, jamais de bornage) tout ce
//          qui en sort : même un appelant compromis ne peut jamais pousser une MaxFreq hors plage
//          matérielle vers le GPU. Le chemin PowerLimit reprend toujours la plage prudente en %
//          via AdlxTuningMath (cette plage-là est absolue).
//   #3 Tout false ou panne native → Result.Fail(HELPER.OP_FAILED), jamais un Ok sur un échec.
//
// Sur une machine sans AMD / sans amdadlx64.dll, IAdlxNative.IsAvailable vaut false et chaque
// appel échoue proprement. RDNA4 n'a plus de contrôle manuel de tension : le chemin Undervolt ne
// change jamais la tension (il renvoie la tension actuelle telle quelle), AdlxNative saute donc
// l'écriture de tension : MaxFreq seule, ce qui marche en réel. A2 : AntiLag (0/1)
// s'écrit par IADLX3DAntiLag.SetEnabled sur la carte dédiée ; toute autre valeur est refusée.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class ADLXTuningActions : IADLXTuningActions
{
    private const string Unavailable = "AMD ADLX unavailable (no AMD GPU / Adrenalin driver).";

    private readonly IAdlxNative _native;

    public ADLXTuningActions() : this(new AdlxNative()) { }

    internal ADLXTuningActions(IAdlxNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue)
    {
        if (gpuIndex < 0)
            return Fail("Invalid GPU index.");
        if (!_native.IsAvailable)
            return Fail(Unavailable);

        try
        {
            return kind switch
            {
                AdlxTuningKind.Undervolt => SetUndervolt(gpuIndex, intValue),
                AdlxTuningKind.PowerLimit => SetPowerLimit(gpuIndex, intValue),

                // Anti-Lag classique, 0 ou 1, sur la carte dédiée (l'index est ignoré : c'est
                // la carte que le lecteur lit, jamais la puce intégrée d'un APU).
                AdlxTuningKind.AntiLag => intValue is 0 or 1
                    ? (_native.EcrireAntiLag(intValue == 1) is { } raison ? Fail(raison) : Result.Ok())
                    : Fail($"Anti-Lag expects 0 or 1, got {intValue}."),
                AdlxTuningKind.FrameRateTarget => intValue != 0
                    ? (_native.EcrireFrtc(intValue) is { } refus ? Fail(refus) : Result.Ok())
                    : Fail("FRTC: 0 is neither a target nor a state."),
                AdlxTuningKind.VerticalRefresh => intValue is >= 0 and <= 3
                    ? (_native.EcrireVsync(intValue) is { } vsyncRefus ? Fail(vsyncRefus) : Result.Ok())
                    : Fail($"Vsync: unknown mode {intValue} (0 to 3)."),

                // Prudence : une valeur d'enum non définie (par exemple un cast hors plage sur le fil)
                // est refusée, jamais de plantage, jamais de faux Ok.
                _ => Fail($"Unsupported ADLX tuning kind '{(int)kind}'."),
            };
        }
        catch (Exception ex)
        {
            // La frontière native avale déjà les pannes (rend false) ; ceci est la garde de
            // dernier recours pour qu'aucune exception ne traverse l'IPC.
            return Fail($"ADLX tuning write failed: {ex.GetType().Name}.");
        }
    }

    // --- Undervolt : une cible absolue d'horloge max GFX (MHz), MaxFreq seule. ---
    // L'appelant (GpuUndervoltTweak) a déjà changé le décalage prudent de l'utilisateur en cible
    // absolue en lisant la MaxFreq actuelle ; l'op inverse porte l'original absolu. Le helper écrit
    // la cible absolue telle quelle (sans +décalage), comme SetPowerLimit : application→retour
    // rend la MaxFreq d'origine exacte.

    private Result SetUndervolt(int gpuIndex, int targetMaxFreqMhz)
    {
        // GET d'abord : lire l'état GFX actuel pour (a) la plage matérielle MaxFreq du pilote
        // (la borne de défense en profondeur plus bas) et (b) la tension actuelle, renvoyée
        // inchangée : sur RDNA4 la tension manuelle n'existe plus, et AdlxNative saute l'écriture
        // de tension quand newVoltMv == tension actuelle (MaxFreq seule, ce qui marche en réel).
        if (!_native.TryReadGfx(gpuIndex, out var current))
            return Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");

        // Défense en profondeur : le helper ne fait jamais confiance à l'appelant pour les bornes. L'op
        // porte une MaxFreq absolue : la plage de règle prudente en décalage [-200,+100] (GpuSafeBounds,
        // couche App) n'a pas de sens ici ; rejeter plutôt (échec propre, jamais de bornage) toute cible
        // hors de la plage matérielle annoncée par le pilote. Même un appelant compromis ne peut jamais
        // pousser une MaxFreq hors plage matérielle sur la vraie puce.
        var range = current.MaxFreqRange;
        if (targetMaxFreqMhz < range.Min || targetMaxFreqMhz > range.Max)
            return Fail(
                $"Refused: ADLX absolute target MaxFreq {targetMaxFreqMhz} MHz is outside the driver's " +
                $"reported hardware range [{range.Min}, {range.Max}] MHz.");

        // SET : AdlxNative relit (ceinture et bretelles), valide la cible contre le
        // GetGPUMaxFrequencyRange du pilote, écrit MaxFreq et saute la tension (inchangée).
        if (!_native.TrySetGfx(gpuIndex, targetMaxFreqMhz, current.VoltMv, out _))
        {
            // Un échec peut laisser une écriture partielle (le retour interne d'AdlxNative est au mieux,
            // son résultat ignoré). Relue ; restée modifiée, rendue ; toujours modifiée, dite : sans snapshot, l'App ne la
            // rendrait jamais.
            if (_native.TryReadGfx(gpuIndex, out var apres) && apres.MaxFreqMhz != current.MaxFreqMhz)
            {
                _native.TrySetGfx(gpuIndex, current.MaxFreqMhz, current.VoltMv, out _);
                if (!_native.TryReadGfx(gpuIndex, out var rendu) || rendu.MaxFreqMhz != current.MaxFreqMhz)
                    return Fail($"ADLX graphics tuning write failed and left MaxFreq at {apres.MaxFreqMhz} MHz (was {current.MaxFreqMhz} MHz); " +
                                "reset GPU tuning to default in AMD Software.");
            }
            return Fail(
                "ADLX graphics tuning write failed (range violation, unsupported GPU, or driver fault).");
        }

        return Result.Ok();
    }

    // --- PowerLimit : une limite de puissance absolue (% du TDP). ---

    private Result SetPowerLimit(int gpuIndex, int pct)
    {
        // Défense en profondeur : refuser ici un pourcentage hors plage (rejet, jamais de bornage).
        if (!AdlxTuningMath.IsPowerLimitWithinHardRail(pct))
            return Fail(
                $"Refused: ADLX power-limit {pct}% is outside the GPU safe-bounds policy band " +
                $"[{AdlxTuningMath.MinPowerLimitPct}, {AdlxTuningMath.MaxPowerLimitPct}]%.");

        // AdlxNative fait GET puis SET : lit l'actuel, valide contre le GetPowerLimitRange
        // du pilote, écrit la limite de puissance.
        if (!_native.TrySetPowerLimit(gpuIndex, pct, out _))
            return Fail(
                "ADLX power-limit write failed (range violation, unsupported GPU, or driver fault).");

        return Result.Ok();
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
