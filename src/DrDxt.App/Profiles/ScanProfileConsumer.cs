using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;

namespace DrDxt.App.Profiles;

// -----------------------------------------------------------------------
// M6 — ScanProfileConsumer: turns a per-chip profile RECOMMENDATION (DrDxt.Profiles)
// into something the scan→apply flow can act on, honestly.
//
// Given the scanned HardwareInventory + SettingsState it:
//   1. asks the IChipProfileMatcher what profile(s) fit this chip (conservative,
//      fail-closed — unknown chip → generic baseline only, never a guess);
//   2. composes the EXECUTED TweakPlan from the union of the recommended profiles'
//      selections, fix-misconfig first (cahier §5), de-duplicated, intersected with the
//      tweaks that actually AppliesTo this machine. GPU-tuning selections are recommended
//      and their targets clamped/overridden, but are not placed in this executed batch —
//      the engine batch is all-or-nothing and the GPU SDK is fail-clean by default, so
//      mixing GPU tuning in would risk rolling back the safe fixes (GPU tuning runs in its
//      own dedicated step, as the apply path already documents);
//   3. OVERRIDES the live GpuTuningTarget / GpuPowerLimitTarget (IGpuTuningTargetSource)
//      from the profile's parameters — every parameter CLAMPED to GpuSafeBounds at the
//      front door (never trust the profile value blindly), with a human note when a value
//      had to be pulled into band;
//   4. surfaces each selection's GainEvidence + its Tested flag so the UI can show the
//      honest "why" and mark a not-yet-validated profile "non vérifié" (cahier §16).
//
// Pure orchestration over the matcher/catalog/target-source — no I/O, no privilege. The
// Real gain is still proven downstream by the engine's A/B re-bench; this never claims a
// number the profile does not cite.
// -----------------------------------------------------------------------

/// <summary>One surfaced profile recommendation line for the scan UI, carrying the honest
/// provenance of the selection (cahier §16). <see cref="IsVerified"/> is false until the
/// owning profile is hardware-validated by the team — the UI marks those "non vérifié".</summary>
public sealed record ProfileRecommendationItem(
    string TweakId,
    ProfileIntent Intent,
    string Claim,
    string Source,
    GainConfidence Confidence,
    SafetyLevel Safety,
    bool Tested,
    bool InExecutedPlan,
    string? AdjustmentNote,
    // 16/09/2026 : le catalogue dit si le levier s'applique a cette machine. L'ecran s'en sert pour
    // ne pas recommander ce qui ne s'applique pas.
    bool Applicable = true,
    // Non-technical, localized category label for the LOCKED (free) view — set by the ScanViewModel
    // which has the localizer. Trailing/defaulted so the consumer's positional construction is
    // source-compatible. Empty until the VM stamps it.
    string CategoryLabel = "")
{
    /// <summary>cahier §16 — a profile not yet validated on real hardware is "non vérifié".</summary>
    public bool IsVerified => Tested;
}

/// <summary>The result of consuming a per-chip profile recommendation for a scanned
/// machine: the executed (safe, non-GPU) plan, the SEPARATE GPU-tuning plan, the clamped
/// GPU target overrides, and the surfaced recommendation lines.</summary>
public sealed record ScanProfileConsumption(
    TweakPlan Plan,
    GpuTuningTarget GpuTuning,
    GpuPowerLimitTarget GpuPowerLimit,
    IReadOnlyList<ProfileRecommendationItem> Items)
{
    /// <summary>The profile's APPLICABLE GPU-tuning selections, as their own plan run by the
    /// dedicated GPU step (see <c>IGpuTuningApplyStep</c>) — SEPARATE from the safe non-GPU
    /// <see cref="Plan"/>, so a GPU failure never rolls back the safe batch. Empty when no GPU
    /// tuning is recommended or the GPU is unsupported (unknown / iGPU / laptop / handheld).</summary>
    public TweakPlan GpuPlan { get; init; } = new(Array.Empty<string>());

    /// <summary>True when there is at least one applicable tweak to execute this increment.</summary>
    public bool HasPlan => Plan.TweakIds.Count > 0;

    /// <summary>True when the profile recommends an APPLICABLE GPU-tuning step for this
    /// machine — the only case the opt-in GPU apply action runs (otherwise it is a no-op).</summary>
    public bool HasGpuPlan => GpuPlan.TweakIds.Count > 0;
}

public interface IScanProfileConsumer
{
    ScanProfileConsumption Consume(HardwareInventory hw, SettingsState state);
}

public sealed class ScanProfileConsumer : IScanProfileConsumer
{
    // Profile parameter keys (mirror DrDxt.Profiles.ChipProfileCatalog).
    private const string KeyCoreOffsetMhz = "CoreOffsetMhz";
    private const string KeyMemoryOffsetMhz = "MemoryOffsetMhz";
    private const string KeyPowerLimitPct = "PowerLimitPct";

    // 08/10/2026 : ces notes s'affichaient telles quelles, en anglais, dans l'App en français. Elles restent la
    // langue du moteur (les tests la lisent) ; la vue les traduit (ScanViewModel.TraduireNote) d'après ces constantes.
    public const string NoteInconnu = "Unknown catalog tweak: skipped.";
    public const string NoteNonLu = "State could not be read on this machine: skipped: "
                                    + "this is not a clean bill of health, nothing was verified here.";
    public const string NoteNonApplicable = "Not applicable to this machine: skipped.";
    public const string NoteReglageCarte = "GPU tuning: tried in game by Optimize, kept only with a measured gain, "
                                           + "otherwise undone; recent crashes that involve the card close it.";

    private readonly IChipProfileMatcher _matcher;
    private readonly ITweakCatalog _catalog;
    private readonly IGpuTuningTargetSource _gpuTargets;

    public ScanProfileConsumer(
        IChipProfileMatcher matcher, ITweakCatalog catalog, IGpuTuningTargetSource gpuTargets)
    {
        _matcher = matcher;
        _catalog = catalog;
        _gpuTargets = gpuTargets;
    }

    public ScanProfileConsumption Consume(HardwareInventory hw, SettingsState state)
    {
        // Start from the conservative defaults; never inherit a previous scan's override.
        _gpuTargets.Reset();
        // Record the AMD-GPU-presence HARDWARE fact from this scan's inventory (independent of
        // the ADLX SDK), so a GPU tweak can surface the honest "AMD GPU detected — tuning coming
        // soon" status (§22.3) instead of a silent skip when ADLX is unavailable. Set even on the
        // fail-closed path below — presence is a fact of the hardware, not of the profile match.
        _gpuTargets.SetAmdGpuPresenceFromHardware(hw);
        var tuning = _gpuTargets.Tuning;
        var powerLimit = _gpuTargets.PowerLimit;

        var rec = _matcher.Recommend(hw);
        if (!rec.IsOk || rec.Value is null)
        {
            // Fail-closed: an unusable inventory yields a safe no-op — empty plan, default
            // targets, nothing claimed. We never fabricate a profile.
            return new ScanProfileConsumption(
                new TweakPlan(Array.Empty<string>()), tuning, powerLimit,
                Array.Empty<ProfileRecommendationItem>());
        }

        // Union of every recommended profile's selections, fix-misconfig first (cahier §5),
        // de-duplicated by tweak id (first / highest-priority occurrence wins).
        var ordered = rec.Value.Profiles
            .SelectMany(p => p.Selections.Select(s => (p.Tested, Selection: s)))
            .OrderBy(x => (int)x.Selection.Intent)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ProfileRecommendationItem>();
        var planIds = new List<string>();
        // GPU-tuning selections that ARE applicable here — run as their own dedicated batch
        // (the dedicated GPU step), never mixed into the safe all-or-nothing plan above.
        var gpuPlanIds = new List<string>();

        foreach (var (tested, sel) in ordered)
        {
            if (!seen.Add(sel.TweakId))
                continue;

            var got = _catalog.Get(sel.TweakId);
            if (got is null || !got.IsOk || got.Value is null)
            {
                // The foundation provenance test guarantees every selection id is a real
                // catalog tweak; still fail-closed if that ever drifts.
                items.Add(Item(sel, tested, inPlan: false, NoteInconnu));
                continue;
            }

            var tweak = got.Value;
            var applicable = tweak.AppliesTo(hw, state);
            var isGpu = tweak.Category == TweakCategory.GpuTuning;

            // GPU parameters → clamp + override the live targets at the FRONT DOOR.
            var clampNote = isGpu ? ApplyGpuOverride(sel, ref tuning, ref powerLimit) : null;

            // Executed batch this increment = applicable non-GPU selections only.
            var inPlan = applicable && !isGpu;
            if (inPlan)
                planIds.Add(sel.TweakId);
            // The SEPARATE GPU batch = applicable GPU-tuning selections. An unsupported GPU
            // (AppliesTo=false on unknown / iGPU / laptop / handheld) drops out here → no GPU
            // batch is ever offered for it.
            else if (isGpu && applicable)
                gpuPlanIds.Add(sel.TweakId);

            string? note = clampNote;
            // 17/09/2026 — « pas lu » et « rien a faire » disaient la meme phrase. Sur la machine
            // de reference, les dix lignes du plan sortaient « Not applicable », dont trois dont
            // l'etat n'avait simplement pas pu etre lu : le client lisait un bilan de sante la ou
            // le produit avouait un trou. On n'ecrit toujours rien sur ce qu'on n'a pas lu — on
            // refuse juste de le faire passer pour une bonne nouvelle.
            if (!applicable && tweak.EtatNonLu(state))
                note = Join(note, NoteNonLu);
            else if (!applicable)
                note = Join(note, NoteNonApplicable);
            // 06/10/2026 (L-GPU) : la carte est ouverte machine par machine ; le veto des arrets se dit a l'ecran.
            else if (isGpu)
                note = Join(note, NoteReglageCarte);

            items.Add(Item(sel, tested, inPlan, note, applicable));
        }

        // Commit the clamped, profile-derived targets (the override) so the GPU tweaks read
        // them when they run. Every value above is already clamped to GpuSafeBounds.
        _gpuTargets.Set(tuning, powerLimit);

        return new ScanProfileConsumption(new TweakPlan(planIds), tuning, powerLimit, items)
        {
            GpuPlan = new TweakPlan(gpuPlanIds),
        };
    }

    // Clamp each GPU parameter to GpuSafeBounds before it reaches a target, returning a
    // human-readable note when a value had to be pulled into band (honest surface).
    private static string? ApplyGpuOverride(
        ProfileTweakSelection sel, ref GpuTuningTarget tuning, ref GpuPowerLimitTarget powerLimit)
    {
        var notes = new List<string>();
        foreach (var p in sel.Parameters)
        {
            switch (p.Key)
            {
                case KeyCoreOffsetMhz:
                {
                    var safe = GpuSafeBounds.ClampCoreOffsetMhz(p.Value);
                    if (safe != p.Value)
                        notes.Add($"core offset clamped {p.Value}→{safe} MHz (GpuSafeBounds)");
                    tuning = tuning with { CoreOffsetMhz = safe };
                    break;
                }
                case KeyMemoryOffsetMhz:
                {
                    var safe = GpuSafeBounds.ClampMemoryOffsetMhz(p.Value);
                    if (safe != p.Value)
                        notes.Add($"memory offset clamped {p.Value}→{safe} MHz (GpuSafeBounds)");
                    tuning = tuning with { MemoryOffsetMhz = safe };
                    break;
                }
                case KeyPowerLimitPct:
                {
                    var safe = GpuSafeBounds.ClampPowerLimitPct(p.Value);
                    if (safe != p.Value)
                        notes.Add($"power limit clamped {p.Value}→{safe}% (GpuSafeBounds)");
                    powerLimit = powerLimit with { PowerLimitPct = safe };
                    break;
                }
                // Unknown parameter keys are ignored — fail-closed: never write a value we
                // do not understand.
            }
        }
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static ProfileRecommendationItem Item(
        ProfileTweakSelection sel, bool tested, bool inPlan, string? note, bool applicable = true) =>
        new(sel.TweakId, sel.Intent, sel.Evidence.Claim, sel.Evidence.Source,
            sel.Evidence.Confidence, sel.Evidence.Safety, tested, inPlan, note, applicable);

    private static string Join(string? a, string b) =>
        string.IsNullOrEmpty(a) ? b : $"{a}; {b}";
}
