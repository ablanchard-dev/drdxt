using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// ADLXTuningActions : les vraies écritures de réglage AMD ADLX risquées (la frontière du m2smoke), miroir de
// NvApiTuningActions. Une seule frontière native, AdlxNative dans DrDxt.GpuNative (validée en réel sur RX 9070 XT / RDNA4,
// emplacements vérifiés contre les en-têtes du SDK AMD ADLX) : aucune seconde copie des emplacements de vtable ici.
//
// Règles d'or, reprises du chemin NVAPI :
//   #1 GET puis SET : AdlxNative lit l'état, valide la cible contre la plage du pilote et n'écrit que le bouton visé.
//      Un GET explicite (TryReadGfx) lit d'abord la tension (renvoyée inchangée) et la plage matérielle MaxFreq.
//   #2 Bornes de défense en profondeur (deux plages distinctes, voulues) :
//        • La plage de règle prudente [-200,+100] MHz (GpuSafeBounds, recopiée dans AdlxTuningMath)
//          est imposée par l'App, puis revérifiée ici autour de la MaxFreq retenue au premier passage
//          du helper depuis le démarrage : l'op porte une MaxFreq absolue, la bande se lit donc sur la carte.
//        • La cible absolue reste aussi dans la plage matérielle annoncée par le pilote
//          (AdlxGfxReads.MaxFreqRange) ; hors de l'une ou de l'autre, rejet (échec propre, jamais de
//          bornage). Après l'écriture, la MaxFreq est relue ; autre que la cible, l'origine est reposée.
//          Le chemin PowerLimit reprend la bande prudente via AdlxTuningMath, en absolu ou en décalage autour de l'usine.
//   #3 Tout false ou panne native → Result.Fail(HELPER.OP_FAILED), jamais un Ok sur un échec.
//
// Sans AMD ou sans amdadlx64.dll, IsAvailable vaut false et chaque appel échoue proprement. RDNA4 n'a plus de tension
// manuelle : le chemin Undervolt renvoie la tension actuelle, AdlxNative saute son écriture (MaxFreq seule, qui marche en
// réel). A2 : AntiLag (0/1) par IADLX3DAntiLag.SetEnabled sur la carte dédiée ; toute autre valeur refusée.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class ADLXTuningActions : IADLXTuningActions
{
    private const string Unavailable = "AMD ADLX unavailable (no AMD GPU / Adrenalin driver).";

    private readonly IAdlxNative _native;
    private readonly Func<int, int, int?> _origineMaxFreq;
    private readonly Func<int, int, int, bool> _noterEcriture;

    public ADLXTuningActions() : this(new AdlxNative(), OrigineAdlx.ParDefaut()) { }

    private ADLXTuningActions(IAdlxNative native, OrigineAdlx origine) : this(native, origine.Retenir, origine.Noter) { }

    /// <param name="origineMaxFreq">L'origine de la MaxFreq retenue au premier passage depuis le démarrage, gardée d'un démarrage
    /// à l'autre (<see cref="OrigineAdlx"/> ; reçoit l'index et la valeur lue, rend l'origine, ou null si la mémoire est
    /// inaccessible : rien n'est écrit). Par défaut des tests : la valeur lue.</param>
    /// <param name="noterEcriture">Note l'origine et la valeur sur le point d'être écrite (index, origine, valeur) ; faux = rien
    /// n'est écrit. Par défaut des tests : vrai.</param>
    internal ADLXTuningActions(IAdlxNative native, Func<int, int, int?>? origineMaxFreq = null, Func<int, int, int, bool>? noterEcriture = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _origineMaxFreq = origineMaxFreq ?? ((_, lue) => lue);
        _noterEcriture = noterEcriture ?? ((_, _, _) => true);
    }

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
                AdlxTuningKind.RemiseUsine => intValue == 0 ? RemettreUsine(gpuIndex) : Fail("Factory reset carries no value."),

                // Anti-Lag classique, 0 ou 1, sur la carte dédiée (l'index est ignoré, jamais la puce intégrée d'un APU).
                AdlxTuningKind.AntiLag => intValue is 0 or 1
                    ? (_native.EcrireAntiLag(intValue == 1) is { } raison ? Fail(raison) : Result.Ok())
                    : Fail($"Anti-Lag expects 0 or 1, got {intValue}."),
                AdlxTuningKind.FrameRateTarget => intValue != 0
                    ? (_native.EcrireFrtc(intValue) is { } refus ? Fail(refus) : Result.Ok())
                    : Fail("FRTC: 0 is neither a target nor a state."),
                AdlxTuningKind.VerticalRefresh => intValue is >= 0 and <= 3
                    ? (_native.EcrireVsync(intValue) is { } vsyncRefus ? Fail(vsyncRefus) : Result.Ok())
                    : Fail($"Vsync: unknown mode {intValue} (0 to 3)."),

                // Une valeur d'enum non définie (cast hors plage sur le fil) : refusée, ni plantage ni faux Ok.
                _ => Fail($"Unsupported ADLX tuning kind '{(int)kind}'."),
            };
        }
        catch (Exception ex)
        {
            // Dernier recours (la frontière native rend déjà false) : aucune exception ne traverse l'IPC.
            return Fail($"ADLX tuning write failed: {ex.GetType().Name}.");
        }
    }

    // --- Undervolt : une cible absolue d'horloge max GFX (MHz), MaxFreq seule. ---
    // GpuUndervoltTweak a converti le décalage en cible absolue ; l'inverse porte l'original absolu. Écrite telle quelle,
    // comme SetPowerLimit : application puis retour rendent la MaxFreq d'origine exacte.

    private Result SetUndervolt(int gpuIndex, int targetMaxFreqMhz)
    {
        // GET d'abord : la plage MaxFreq du pilote (borne plus bas) et la tension actuelle, renvoyée inchangée (AdlxNative
        // saute l'écriture de tension quand elle est égale).
        if (!_native.TryReadGfx(gpuIndex, out var current))
            return Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");

        // Défense en profondeur : le helper ne fait jamais confiance à l'appelant pour les bornes. Rejet (échec propre,
        // jamais de bornage) de toute cible hors de la plage matérielle annoncée par le pilote.
        var range = current.MaxFreqRange;
        if (targetMaxFreqMhz < range.Min || targetMaxFreqMhz > range.Max)
            return Fail(
                $"Refused: ADLX absolute target MaxFreq {targetMaxFreqMhz} MHz is outside the driver's " +
                $"reported hardware range [{range.Min}, {range.Max}] MHz.");

        // La plage du pilote monte jusqu'au plafond d'overclock, où un programme du compte pourrait pousser la MaxFreq : la
        // bande de l'App ([-200, +100] MHz, GpuSafeBounds) s'applique aussi ici, autour d'une origine gardée d'un démarrage
        // à l'autre (OrigineAdlx), sinon des écritures répétées monteraient de +100 en +100. Une valeur réglée par le joueur
        // entre deux démarrages devient l'origine. Au-delà de la bande, seule la remise à l'usine (RemiseUsine, geste du
        // joueur) rend : ici, rien n'est écrit, et le refus le dit.
        if (current.MaxFreqMhz == targetMaxFreqMhz)
            return Result.Ok(); // déjà là (une remise à l'usine précédente, par exemple) : rien à écrire
        if (_origineMaxFreq(gpuIndex, current.MaxFreqMhz) is not { } origine)
            return Fail("Refused: the GPU clock read at this boot could not be retained; nothing written.");
        if ((long)targetMaxFreqMhz > (long)origine + AdlxTuningMath.MaxCoreOffsetMhz)
            return Result.Fail(ErrorCodes.GpuRemiseUsineRequise, Docteur.T("ADLX.UsineRequise", targetMaxFreqMhz));
        if ((long)targetMaxFreqMhz < (long)origine + AdlxTuningMath.MinCoreOffsetMhz)
            return Fail(
                $"Refused: ADLX target MaxFreq {targetMaxFreqMhz} MHz is outside the safe band " +
                $"[{AdlxTuningMath.MinCoreOffsetMhz}, +{AdlxTuningMath.MaxCoreOffsetMhz}] MHz around {origine} MHz read at this boot.");

        // Notée avant l'écriture : une valeur posée sans trace serait prise pour celle du joueur au démarrage suivant.
        if (!_noterEcriture(gpuIndex, origine, targetMaxFreqMhz))
            return Fail("Refused: the GPU clock origin could not be kept across boots; nothing written.");

        // SET : AdlxNative relit, valide contre GetGPUMaxFrequencyRange, écrit MaxFreq, saute la tension.
        if (!_native.TrySetGfx(gpuIndex, targetMaxFreqMhz, current.VoltMv, out _))
        {
            // Un échec peut laisser une écriture partielle (le retour interne d'AdlxNative est au mieux) : relue, rendue si
            // modifiée, dite si elle le reste (sans snapshot, l'App ne la rendrait jamais).
            if (_native.TryReadGfx(gpuIndex, out var apres) && apres.MaxFreqMhz != current.MaxFreqMhz)
            {
                _native.TrySetGfx(gpuIndex, current.MaxFreqMhz, current.VoltMv, out _);
                var relu = _native.TryReadGfx(gpuIndex, out var rendu);
                NoterLaCarte(gpuIndex, origine, relu ? rendu.MaxFreqMhz : null);
                if (!relu || rendu.MaxFreqMhz != current.MaxFreqMhz)
                    return Result.Fail(ErrorCodes.TweakPartiallyApplied, $"ADLX graphics tuning write failed and left MaxFreq at {apres.MaxFreqMhz} MHz (was {current.MaxFreqMhz} MHz); " +
                                "reset GPU tuning to default in AMD Software.");
            }
            else NoterLaCarte(gpuIndex, origine, current.MaxFreqMhz);
            return Fail(
                "ADLX graphics tuning write failed (range violation, unsupported GPU, or driver fault).");
        }

        // Relue : un pilote qui arrondit ou ramène la cible fait reposer l'origine ; Ok seulement sur la valeur écrite.
        if (_native.TryReadGfx(gpuIndex, out var relue) && relue.MaxFreqMhz == targetMaxFreqMhz)
            return Result.Ok();
        _native.TrySetGfx(gpuIndex, current.MaxFreqMhz, current.VoltMv, out _);
        var relue2 = _native.TryReadGfx(gpuIndex, out var rendue);
        NoterLaCarte(gpuIndex, origine, relue2 ? rendue.MaxFreqMhz : null);
        return relue2 && rendue.MaxFreqMhz == current.MaxFreqMhz
            ? Fail($"ADLX MaxFreq was not read back at {targetMaxFreqMhz} MHz; the original {current.MaxFreqMhz} MHz is back.")
            : Result.Fail(ErrorCodes.TweakPartiallyApplied, $"ADLX MaxFreq was not read back at {targetMaxFreqMhz} MHz and the original {current.MaxFreqMhz} MHz could not be restored; " +
                   "reset GPU tuning to default in AMD Software.");
    }

    // Après un échec, la dernière relecture est notée telle quelle : la valeur d'avant reposée n'est pas prise pour un
    // réglage du joueur au démarrage suivant. Non relue : la cible reste notée, un démarrage qui lit autre chose repart
    // de la valeur lue.
    private void NoterLaCarte(int gpuIndex, int origine, int? lue)
    {
        if (lue is { } v) _noterEcriture(gpuIndex, origine, v);
    }

    // La remise à l'usine (AdlxTuningKind.RemiseUsine), geste à part : le seul retour au défaut du pilote au-delà de la
    // bande, qui remet aussi mémoire, ventilateur et puissance d'AMD Software (l'appelant le dit). Le code 0 ne prouve
    // rien : la carte relue doit se dire à l'usine (IsAtFactory).
    private Result RemettreUsine(int gpuIndex)
        => _native.RemettreReglageUsine(gpuIndex) is { } refus ? Fail(Docteur.T("ADLX.UsineImpossible", refus))
            : _native.EstALUsine(gpuIndex) is true ? Result.Ok()
            : Fail(Docteur.T("ADLX.UsineImpossible", "IsAtFactory does not confirm it"));

    // --- PowerLimit : une limite de puissance dans l'unité du pilote (% absolu ou décalage, voir AdlxTuningMath). ---

    private Result SetPowerLimit(int gpuIndex, int pct)
    {
        // Défense en profondeur : refuser ici un pourcentage hors plage (rejet, jamais de bornage).
        if (!AdlxTuningMath.IsPowerLimitWithinHardRail(pct))
            return Fail(
                $"Refused: ADLX power-limit {pct}% is outside the GPU safe-bounds policy band " +
                $"[{AdlxTuningMath.MinPowerLimitPct}, {AdlxTuningMath.MaxPowerLimitPct}]% " +
                $"(offset [{AdlxTuningMath.MinPowerLimitOffset}, {AdlxTuningMath.MaxPowerLimitOffset}]).");

        // GET puis SET dans AdlxNative, validé contre GetPowerLimitRange.
        if (!_native.TrySetPowerLimit(gpuIndex, pct, out _))
            return Fail(
                "ADLX power-limit write failed (range violation, unsupported GPU, or driver fault).");

        return Result.Ok();
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
