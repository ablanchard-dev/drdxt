using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuUndervoltTweak.
//
// Un décalage d'horloge du cœur (et de la mémoire) par NVAPI, ou la commande d'undervolt d'ADLX sur
// AMD, pas une courbe tension-fréquence complète. Ce n'est pas « les mêmes fréquences à tension plus
// basse » : ce levier écrit un décalage d'horloge borné à [-200, +100] MHz (GpuSafeBounds), dans un
// sens ou dans l'autre, et aucune tension. Le seul profil livré lui passe +75 MHz : un léger overclock
// testé. Un vrai undervolt (courbe V/F) n'est pas dans ce produit ; l'id garde son nom (clé de persistance).
//
// Étapes (chaque frontière externe protégée : échec propre, jamais d'exception) :
//   (1) AppliesTo : carte dédiée de PC de bureau seulement (GpuGating) ;
//   (2) bornes vérifiées par GpuSafeBounds ; hors bornes : TWEAK.NOT_APPLICABLE ;
//   (3) décalage d'origine relu, rangé en op inverse sérialisée ;
//   (4) décalage posé par IElevatedClient (op typée autorisée) ;
//   (5) test de stabilité (IStabilityTester) : s'il n'est pas clairement stable, le décalage est rendu
//       aussitôt et le levier rend TWEAK.UNSTABLE. Jamais Ok sur une carte instable, jamais un
//       décalage non vérifié gardé.
//
// RevertAsync (base) rend le décalage d'origine relu.
// -----------------------------------------------------------------------
/// <summary>Pose un décalage d'horloge borné sur la carte graphique, gardé seulement s'il passe le test de stabilité.
/// Proposé sur un PC de bureau à carte dédiée.</summary>
public sealed class GpuUndervoltTweak : GpuTweakBase
{
    // Borne haute du test de stabilité en ligne : un test bloqué ne doit pas tenir le thread
    // de pose pendant que le décalage est actif. Au-delà, le verdict vaut Inconclusive → retour.
    private static readonly TimeSpan StabilityProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly INvApiGpuTuning _nvApi;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IStabilityTester _stability;
    private readonly IGpuTuningTargetSource _targets;

    public GpuUndervoltTweak(
        INvApiGpuTuning nvApi,
        IAdlxGpuTuning adlx,
        IStabilityTester stability,
        IElevatedClient elevated,
        IGpuTuningTargetSource targets)
        : base(elevated)
    {
        _nvApi = nvApi;
        _adlx = adlx;
        _stability = stability;
        _targets = targets;
    }

    // La cible de réglage en cours. Un profil de puce a pu la remplacer au scan (déjà bornée
    // à GpuSafeBounds à l'entrée) ; relue ici et rebornée plus bas avant toute écriture
    // (défense en profondeur).
    private GpuTuningTarget Target => _cibleExpert ?? _targets.Tuning;
    private GpuTuningTarget? _cibleExpert;

    public override string Id => "gpu.undervolt";
    public override string SummaryKey => "tweak.gpu.undervolt.efficiency";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        GpuGating.HasDesktopDiscreteGpu(hw);

    // Le bail du helper, armé avant l'écriture quand le mode Expert le demande.
    private (string Id, int Secondes)? _bailDemande;
    private HeartbeatSender? _bail;

    /// <summary>Pose avec le bail du helper armé avant l'écriture : si DrDXT s'arrête ou plante, la carte revient
    /// seule. Sans bail, rien n'est écrit. Le battement rendu doit vivre tant que le décalage est gardé.</summary>
    public async Task<Result<(TweakSnapshot Snapshot, HeartbeatSender Bail)>> ApplyAvecBailAsync(string idBail, int secondes,
        GpuTuningTarget cible)
    {
        _bailDemande = (idBail, secondes);
        _bail = null;
        _cibleExpert = cible;
        try
        {
            var r = await ApplyAsync().ConfigureAwait(false);
            if (r.IsOk && _bail is { } bail) return Result<(TweakSnapshot, HeartbeatSender)>.Ok((r.Value!, bail));
            // Rendu (ou jamais écrit) : bail libéré, sinon il rejouait l'origine 120 s plus tard par-dessus un réglage posé
            // entre-temps. Partiel : il reste, le helper rendra seul.
            if (_bail is { } orphelin)
            {
                if (r.Error?.Code == ErrorCodes.TweakPartiallyApplied) await orphelin.DisposeAsync().ConfigureAwait(false);
                else await orphelin.LibererAsync().ConfigureAwait(false);
            }
            return Result<(TweakSnapshot, HeartbeatSender)>.Fail(
                r.Error?.Code ?? ErrorCodes.HelperOpFailed, r.Error?.Message ?? Docteur.T("GX.BailNonArme"));
        }
        finally
        {
            _bailDemande = null;
            _bail = null;
            _cibleExpert = null;
        }
    }

    /// <summary>Pose une cible donnée, sans bail : pour le banc de validation, dont le canal élevé vit dans son propre
    /// processus (un bail n'y rendrait rien) et dont le journal rend l'essai au lancement suivant. Mêmes bornes, même
    /// sonde d'écriture et même test de stabilité que la pose normale.</summary>
    internal async Task<Result<TweakSnapshot>> ApplyAvecCibleAsync(GpuTuningTarget cible)
    {
        _cibleExpert = cible;
        try { return await ApplyAsync().ConfigureAwait(false); }
        finally { _cibleExpert = null; }
    }

    /// <summary>null = rien à armer ou bail armé ; sinon le refus, et l'écriture n'a pas lieu.</summary>
    private async Task<Result?> ArmerLeBailSiDemandeAsync(PrivilegedOp inverse)
    {
        if (_bailDemande is not { } d) return null;
        var hb = await EstablishAutoRevertLeaseAsync(inverse, d.Id, d.Secondes).ConfigureAwait(false);
        if (!hb.IsOk) return Result.Fail(hb.Error!.Value.Code, Docteur.T("GX.BailCarte", hb.Error.Value.Message));
        _bail = hb.Value;
        return null;
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // (2) Borner et valider d'abord : rien hors bornes ne doit atteindre le helper.
            if (!GpuSafeBounds.IsCoreOffsetSafe(Target.CoreOffsetMhz)
                || !GpuSafeBounds.IsMemoryOffsetSafe(Target.MemoryOffsetMhz))
            {
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable,
                    "Requested GPU offset is outside the conservative safe bounds.");
            }

            if (_nvApi.IsAvailable)
                return await ApplyNvidiaAsync().ConfigureAwait(false);
            if (_adlx.IsAvailable)
                return await ApplyAmdAsync().ConfigureAwait(false);

            // Jamais de saut silencieux. Si un GPU AMD est présent (fait matériel tiré de l'inventaire,
            // indépendant du SDK ADLX) mais qu'aucun SDK de réglage n'est branché, on affiche un état
            // explicite et honnête : ni erreur générique, ni succès, ni « non applicable » silencieux.
            if (_targets.IsAmdGpuPresent)
                return Result<TweakSnapshot>.Fail(ErrorCodes.GpuAmdTuningUnavailable,
                    "AMD GPU detected: ADLX tuning SDK not available in this build.");

            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Neither NVAPI nor ADLX is available for GPU undervolt.");
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU undervolt apply boundary error: {ex.Message}");
        }
    }

    /// <summary>Pur : un autre outil règle-t-il déjà la carte ? Décalage cœur non nul et différent de la cible de DrDXT, ou
    /// décalage mémoire non nul (DrDXT n'écrit jamais la mémoire).</summary>
    public static bool ReglageTiers(int coeurMhz, int memoireMhz, int cibleCoeurMhz)
        => (coeurMhz != 0 && coeurMhz != cibleCoeurMhz) || memoireMhz != 0;

    private async Task<Result<TweakSnapshot>> ApplyNvidiaAsync()
    {
        // (3) Relever le vrai décalage d'origine avant d'écrire (retour arrière fidèle).
        // Non lu n'est jamais 0 : sans les deux décalages, DrDXT ne sait pas si un autre outil règle déjà la carte, et refuse.
        var core = _nvApi.GetCoreClockOffsetMhz(Target.GpuIndex);
        if (!core.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuDecalageNonLu, $"core: {core.Error?.Code}");
        var memoire = _nvApi.GetMemoryClockOffsetMhz(Target.GpuIndex);
        if (!memoire.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuDecalageNonLu, $"memory: {memoire.Error?.Code}");
        // Un décalage déjà posé par un autre outil : DrDXT refuse plutôt que d'empiler ou d'écraser. Seul le décalage cœur que
        // DrDXT pose lui-même (même valeur que sa cible) passe ; la mémoire, que DrDXT n'écrit jamais, doit être à zéro.
        if (ReglageTiers(core.Value, memoire.Value, Target.CoreOffsetMhz))
            return Result<TweakSnapshot>.Fail(ErrorCodes.GpuReglageTiers,
                $"core {core.Value:+0;-0;0} MHz, memory {memoire.Value:+0;-0;0} MHz");
        // La mémoire ne s'écrit jamais (le helper n'applique que le cœur) : son champ part à 0. Y mettre la mémoire de la
        // carte ferait refuser par la liste blanche un réglage cœur sur une carte déjà montée à +1000 MHz par un autre outil.
        var inverse = new ApplyNvApiClockOffsetOp(Target.GpuIndex, core.Value, MemoryOffsetMhz: 0);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) Sonde de capacité : prouver que l'écriture marche sur cette carte et ce pilote
        // avant de toucher à quoi que ce soit. On rejoue l'op inverse (réécriture à l'identique de
        // la valeur relevée, aucun changement matériel) et on relit. Une carte dont le pilote dit
        // « réussi » sans rien écrire est prise ici, au lieu de produire un faux « appliqué ».
        // Échec → TWEAK.UNSUPPORTED_ON_THIS_GPU, rien n'est réglé.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _nvApi.GetCoreClockOffsetMhz(Target.GpuIndex), core.Value,
            "GPU core-clock offset").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        // Le bail avant l'écriture, quand le mode Expert le demande.
        if (await ArmerLeBailSiDemandeAsync(inverse).ConfigureAwait(false) is { } refusNv)
            return Result<TweakSnapshot>.Fail(refusNv.Error!.Value.Code, refusNv.Error.Value.Message);

        // (4) Poser le décalage demandé par l'op typée sur liste blanche.
        var applied = await Elevated
            .ExecuteAsync(
                new ApplyNvApiClockOffsetOp(Target.GpuIndex, Target.CoreOffsetMhz, MemoryOffsetMhz: 0),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // (5) Test de stabilité → retour immédiat si ce n'est pas clairement stable.
        return await VerifyStableOrRevertAsync(inverse, snapshotJson.Value, Target.GpuIndex).ConfigureAwait(false);
    }

    private async Task<Result<TweakSnapshot>> ApplyAmdAsync()
    {
        // Trouver d'abord le GPU AMD dédié réglable. Sur une machine iGPU + dGPU (par exemple un APU
        // Ryzen X3D à l'index 0 + une Radeon dédiée à l'index 1), le Target.GpuIndex demandé (0 en
        // dur) est l'iGPU non réglable : le lire ou l'écrire échouerait avant même la vraie carte.
        // La couture de lecture suit le sélecteur du banc AMD et rend le premier index dont le
        // réglage GFX se lit. Échec propre (rien de posé) si aucun GPU AMD réglable n'est trouvé.
        var resolved = _adlx.ResolveTunableGpuIndex();
        if (!resolved.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not resolve a tunable AMD GPU: {resolved.Error?.Code}.");
        int gpuIndex = resolved.Value;

        // ADLX lit et écrit un MaxFreq absolu (MHz), pas un décalage. On lit l'horloge max absolue
        // courante pour (a) construire la cible absolue écrite et (b) relever l'origine absolue comme
        // op inverse. Même logique que la limite de puissance (qui relève et rejoue un % absolu) :
        // pose→retour rend exactement le MaxFreq d'usine. (GetUndervoltValue rend une tension et ne
        // sert que de sonde de lisibilité à ResolveTunableGpuIndex : elle ne doit jamais nourrir
        // l'op de retour, sinon les unités se mélangent.)
        var cur = _adlx.GetGfxMaxFreqMhz(gpuIndex);
        if (!cur.IsOk)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Could not read current GFX max frequency: {cur.Error?.Code}.");

        // Target.CoreOffsetMhz est le décalage prudent demandé, déjà borné par GpuSafeBounds à
        // l'entrée (et revalidé dans ApplyAsync). Cible absolue = courante + décalage.
        int targetMaxFreqMhz = cur.Value + Target.CoreOffsetMhz;

        // Inverse = le MaxFreq d'origine absolu relevé avant la pose → le retour le rend exactement.
        var inverse = new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.Undervolt, cur.Value);
        var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
        if (!snapshotJson.IsOk || snapshotJson.Value is null)
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

        // (3b) Sonde de capacité : prouver que l'écriture marche sur cette carte et ce pilote
        // avant de toucher à quoi que ce soit. On rejoue l'op inverse (réécriture à l'identique de
        // la valeur relevée, aucun changement matériel) et on relit. Une carte dont le pilote dit
        // « réussi » sans rien écrire est prise ici, au lieu de produire un faux « appliqué ».
        // Échec → TWEAK.UNSUPPORTED_ON_THIS_GPU, rien n'est réglé.
        var probe = await GpuWriteProbe.ConfirmWritableAsync(
            Elevated, inverse, () => _adlx.GetGfxMaxFreqMhz(gpuIndex), cur.Value,
            "GPU max-frequency").ConfigureAwait(false);
        if (!probe.IsOk)
            return Result<TweakSnapshot>.Fail(probe.Error!.Value.Code, probe.Error!.Value.Message);

        if (await ArmerLeBailSiDemandeAsync(inverse).ConfigureAwait(false) is { } refusAmd)
            return Result<TweakSnapshot>.Fail(refusAmd.Error!.Value.Code, refusAmd.Error.Value.Message);
        var applied = await Elevated
            .ExecuteAsync(
                new ApplyAdlxTuningOp(gpuIndex, AdlxTuningKind.Undervolt, targetMaxFreqMhz),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!applied.IsOk)
            return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

        // Relevé, écriture, retour et stabilité visent tous le même GPU physique trouvé.
        return await VerifyStableOrRevertAsync(inverse, snapshotJson.Value, gpuIndex).ConfigureAwait(false);
    }

    /// <summary>Lance le test de stabilité ; si le GPU n'est pas clairement stable, rejoue
    /// l'op inverse de l'origine relevée pour annuler aussitôt et rend TWEAK.UNSTABLE.
    /// Seul un verdict Stable net donne Ok avec le snapshot.
    /// Si l'appel de retour échoue lui-même, on ne prétend jamais avoir annulé : le message dit
    /// honnêtement que le retour automatique a échoué et renvoie au point de restauration
    /// (le GPU reste réglé et instable).</summary>
    private async Task<Result<TweakSnapshot>> VerifyStableOrRevertAsync(PrivilegedOp inverse, string snapshotJson, int gpuIndex)
    {
        // Borner le test de stabilité en ligne : un test bloqué ou détraqué ne doit jamais tenir
        // le thread de pose avec le décalage actif. Un délai dépassé (OperationCanceledException)
        // ou toute panne du test vaut « pas stable » (Inconclusive) → on continue et on annule
        // aussitôt, comme la sécurité existante (seul un Stable net garde le décalage). Plus sûr
        // qu'une attente avec CancellationToken.None, où une exception ou un blocage du test
        // sauterait le retour et laisserait le GPU réglé.
        bool stable;
        try
        {
            using var timeout = new CancellationTokenSource(StabilityProbeTimeout);
            var stability = await _stability.RunAsync(gpuIndex, timeout.Token).ConfigureAwait(false);
            stable = stability.IsOk && stability.Value == StabilityOutcome.Stable;
        }
        catch (Exception)
        {
            stable = false; // délai dépassé ou panne du banc → Inconclusive → retour plus bas.
        }

        if (stable)
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson));

        // Instable / non concluant / erreur du banc → on rend le décalage tout de suite et on
        // vérifie le résultat. Un résultat de retour ignoré pourrait laisser le GPU réglé et
        // instable pendant qu'on prétend à tort l'avoir rendu.
        Result revert;
        try
        {
            // Ce retour annule la pose : hors portée, sinon le helper le rejouerait et remettrait le décalage instable.
            using (PorteeDePose.Suspendre())
                revert = await Elevated.ExecuteAsync(inverse, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            revert = Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }

        if (!revert.IsOk)
        {
            // Échec honnête et distinct : ne pas prétendre l'avoir rendu. Un code a part,
            // TweakPartiallyApplied, pour que le journal reste et que l'ecran ne dise jamais « rendue ».
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakPartiallyApplied,
                "GPU undervolt failed its stability test AND the automatic revert failed " +
                $"({revert.Error?.Code}: {revert.Error?.Message}): the GPU may still be undervolted; " +
                "use the System Restore point created before this change to restore stock clocks.");
        }

        return Result<TweakSnapshot>.Fail(ErrorCodes.TweakUnstable,
            "GPU undervolt failed its stability test and was reverted immediately.");
    }
}
