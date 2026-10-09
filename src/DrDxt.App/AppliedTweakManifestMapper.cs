using System.Collections.Generic;
using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.App;

// -----------------------------------------------------------------------
// AppliedTweakManifestMapper — the App-side bridge from a VALIDATED apply to the
// reconciler's desired-state manifest (the persistence moat, §9.12 / M4 plan §2).
//
// The apply pipeline already MEASURES and auto-reverts regressions, so the tweaks
// still KEPT after ApplyAsync (Applied && !AutoReverted) ARE the validated set. This
// maps each kept outcome to a reconciler AppliedTweak so the App can commit them.
//
// It bridges two real mismatches with an EXPLICIT, reviewed CLOSED table:
//
//   (1) id NAMESPACE. The Application catalog keys tweaks by ids like "fixit.power_plan"
//       / "latency.reflex"; the reconciler policy + descriptor catalog key by
//       "fixit.sanePowerPlan" / "latency.reflex.lowLatencyMode" (see
//       DefaultReconcilabilityPolicy / DefaultReconcilableTweakCatalog — those layers
//       deliberately do not reference Application). This table is the single, reviewed
//       crosswalk between the two id spaces.
//
//   (2) DESIRED value. The ApplyReport's TweakSnapshot carries only the original value
//       (the inverse op); the canonical DESIRED token the reconciler re-applies is known
//       per tweak (Ultra=2, the High-Performance plan GUID, service "running") and is
//       produced here via the same ReconcilableValueCanonicalizer the probe reads with,
//       so a committed desired token byte-matches a later probe read (ordinal compare).
//
// SCOPE: only tweaks with a reconcilable OS-side surface AND a known desired token are
// emitted. A kept tweak that is not in the table (GPU tuning, or a composite whose only
// reconciler-owned surface is one leaf) is EXCLUDED — the manifest holds only validated,
// reconcilable desired state. This is belt-and-suspenders with SignedDesiredStateStore,
// which independently rejects any non-reconcilable id at commit.
//
// Original-value caveat (flagged for review): OriginalValueJson is recorded from the
// kept snapshot AS CAPTURED (the inverse-op payload), an honest record of the prior
// state. It is used by the reconciler only for drift CLASSIFICATION (user-intentional vs
// Windows-reset), never to re-apply; the DESIRED token is the load-bearing field and is
// exact. A future increment can canonicalize the original token to byte-match the probe
// if drift classification needs it; the security/reapply path does not depend on it.
// -----------------------------------------------------------------------
public sealed class AppliedTweakManifestMapper
{
    /// <summary>The canonical desired token for the active power plan once
    /// SanePowerPlanTweak has switched it to High Performance — the same 'D' form the
    /// reconciler probe reads (so committed desired == probed current under ordinal).</summary>
    public static readonly string HighPerformancePlanToken =
        ReconcilableValueCanonicalizer.PowerPlan(KnownPowerPlans.HighPerformance);

    // Application tweakId → (reconciler tweakId, canonical desired token). A CLOSED,
    // reviewed table mirroring the reconciler descriptor catalog's read locations.
    private static readonly IReadOnlyDictionary<string, (string ReconcilerId, string DesiredToken)> Map =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // SanePowerPlanTweak switches the active plan to High Performance.
            ["fixit.power_plan"] = ("fixit.sanePowerPlan", HighPerformancePlanToken),

            // NvidiaUltraLowLatencyTweak writes LowLatencyMode = Ultra (2) on the NVTweak leaf.
            ["latency.reflex"] = ("latency.reflex.lowLatencyMode", "2"),

            // X3dCcdParkingFixTweak starts the AMD 3D V-Cache optimizer service. The
            // reconciler owns the SERVICE surface (its single volatile OS-side surface);
            // the desired token is the canonical "running" state.
            ["fixit.x3d_ccd"] = (
                "fixit.x3dVCacheService",
                ReconcilableValueCanonicalizer.Service(ServiceRunState.Running)),

            // NS-1 HagsOnTweak writes HwSchMode = 2 (On) on the HKLM GraphicsDrivers leaf.
            // HKLM is the one surface the LocalSystem boot reconciler reapplies correctly.
            ["latency.hagsOn"] = ("os.hags", ReconcilableValueCanonicalizer.Dword(2)),

            // Intentionally not reconciled by the LocalSystem boot reconciler:
            //   * latency.gameDvrBackgroundOff — an HKCU leaf. The reconciler reapplies
            //     IN-PROCESS as LocalSystem (no UAC at boot), where Registry.CurrentUser is
            //     SYSTEM's hive, not the logged-in user's — so an HKCU reapply would write the
            //     wrong hive (HostInstaller scopes reconcile to HKLM/power-plan/service). The
            //     value persists natively across reboot.
            //
            //     CORRECTION 21/08/2026 — cette note disait aussi « et l'App le reapplique au
            //     lancement suivant ». C'est faux : `App.OnStartup` construit l'injection, affiche
            //     la fenetre, et c'est tout. Aucune reverification, aucune reapplication. Le filet
            //     invoque n'existe pas : si un tiers (autre « optimiseur », mise a jour de
            //     fonctionnalite Windows, l'utilisateur) defait ce reglage, rien ne le remarque —
            //     alors que le north star promet « persistante ».
            //     Ce qui manque vraiment, dans l'ordre : (1) un enregistrement par UTILISATEUR de
            //     ce qui a ete applique (le manifeste signe ne les porte pas, justement parce
            //     qu'ils sont exclus ici) ; (2) une detection de derive au lancement ; (3) une
            //     notification via `IReconcilerNotifier`, qui NOTIFIE sans ecraser (regle I5).
            //     La reapplication devra passer par le TWEAK lui-meme, pas par une op typee :
            //     le tweak sait deja gerer le composite a deux feuilles et la fusion du REG_SZ.
            //   * latency.gameModeOn  — COMPOSITE (two HKCU leaves) + the HKCU concern above;
            //     the one-id->one-leaf model can't express it. Needs multi-leaf reconcile.
            //   * latency.vrrFlipModelOn — a MERGED REG_SZ (HKCU); a naive overwrite-reapply
            //     would CLOBBER user keys (e.g. AutoHDR). Needs a merge-aware, per-user reapply.
            // All mirror the existing NVAPI/ADLX-backed omissions: omitted on purpose, not lost.
        };

    /// <summary>06/10/2026 — le nom sous lequel le service connaît ce levier, s'il le réaffirme.</summary>
    public static string? IdDuService(string id) => Map.TryGetValue(id, out var m) ? m.ReconcilerId : null;

    /// <summary>06/10/2026 (plan 4.3) — les leviers gardés que le service ne réaffirme pas (absents de la table
    /// ci-dessus : les leviers HKCU, le fichier d'échange) n'entraient pas du tout au manifeste, et la désinstallation
    /// ne relit que lui : elle laissait leur réglage en place. Leur origine y entre en <c>RevertOnly</c> (rendue à la
    /// désinstallation, jamais réaffirmée), comme le fait déjà un levier gardé en jeu (TweakCandidate.Garder).
    /// Seulement une origine que la désinstallation sait rendre : un réglage de carte graphique n'y entre pas.</summary>
    public IReadOnlyList<AppliedTweak> ToRevertOnly(ApplyReport report)
    {
        var quand = DateTimeOffset.UtcNow;
        return report.Outcomes
            // Revue du 06/10 (P1) : sans valeur posée, la désinstallation ne sait pas si la valeur est encore
            // celle de DrDXT et n'y touche jamais (le fichier d'échange) : l'inscrire ferait croire qu'elle la rend.
            .Where(o => o.Applied && !o.AutoReverted && o.Snapshot is { AppliedValueJson: { Length: > 0 } }
                        && !Map.ContainsKey(o.TweakId) && OrigineRendable(o.Snapshot.OriginalValueJson))
            .Select(o => new AppliedTweak(o.TweakId, o.Snapshot!.AppliedValueJson!, o.Snapshot.OriginalValueJson, quand))
            .ToList();
    }

    // Les ops que le mode de désinstallation du helper sait exécuter (même liste que OpAllowlist.ValiderOrigine).
    private static bool OrigineRendable(string origine)
    {
        static bool Rendable(PrivilegedOp op) => op is SetRegistryValueOp or DeleteRegistryValueOp or SetActivePowerPlanOp
            or SetProcessorBoostModeOp
            or ApplyNvApiDrsSettingOp or DeleteNvApiDrsSettingOp or RunServiceControlOp
            // 08/10/2026 : les réglages de la carte que la désinstallation sait relire et rendre (jamais la sous-tension).
            or ApplyNvApiPowerLimitOp
            or ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit or AdlxTuningKind.AntiLag or AdlxTuningKind.FrameRateTarget or AdlxTuningKind.VerticalRefresh };
        try
        {
            if (PrivilegedOpCodec.Deserialize(origine) is { IsOk: true, Value: { } une }) return Rendable(une);
            return CompositeSnapshotCodec.Decode(origine) is { IsOk: true, Value: { Count: > 0 } toutes } && toutes.All(Rendable);
        }
        catch (Exception) { return false; }
    }

    /// <summary>The validated, reconcilable desired-state tweaks to commit, built from
    /// the KEPT (Applied &amp;&amp; !AutoReverted) outcomes of a successful apply. Auto-reverted,
    /// snapshot-less, and unmapped/non-reconcilable outcomes are excluded.</summary>
    public IReadOnlyList<AppliedTweak> ToDesiredTweaks(ApplyReport report)
    {
        // ApplyOutcome carries no per-tweak timestamp; the manifest's AppliedTweak wants
        // one. Use a single commit-moment stamp for the whole batch — the manifest's own
        // CommittedAt is the authoritative time and the reconciler does not key behaviour
        // off per-tweak AppliedAt.
        var appliedAt = DateTimeOffset.UtcNow;

        var tweaks = new List<AppliedTweak>();
        foreach (var outcome in report.Outcomes)
        {
            // Only tweaks that were applied AND not auto-reverted are validated/kept.
            if (!outcome.Applied || outcome.AutoReverted)
                continue;

            // No snapshot ⇒ no honest original value to record ⇒ skip (defensive).
            if (outcome.Snapshot is null)
                continue;

            // Only ids with a known reconciler crosswalk + desired token are committed.
            if (!Map.TryGetValue(outcome.TweakId, out var mapping))
                continue;

            tweaks.Add(new AppliedTweak(
                mapping.ReconcilerId,
                mapping.DesiredToken,
                outcome.Snapshot.OriginalValueJson,
                appliedAt,
                // 11/09/2026 — l'origine complete des composites. Le token ci-dessus ne nomme qu'une
                // feuille (celle que le reconciliateur reaffirme). Un levier composite (X3D : service +
                // CppcEnable + Game Bar) en pose plusieurs : sans ce champ, la desinstallation ne savait
                // pas quelle valeur DrDXT avait mise sur les deux autres, donc elle ne pouvait pas les
                // juger rendables — et le tout-ou-rien bloquait le composite entier, pour toujours.
                // Le tweak connaissait deja ses valeurs posees (TweakSnapshot.AppliedValueJson) : on les
                // inscrit telles quelles, sans les recalculer ici.
                outcome.Snapshot.AppliedValueJson));
        }

        return tweaks;
    }
}
