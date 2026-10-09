using DrDxt.Core;

namespace DrDxt.Profiles;

/// <summary>The honest "why" behind a recommendation: which families were detected and a
/// human-readable reason for each decision (for the UI/telemetry, never a guess).</summary>
public sealed record ChipClassification(
    bool IsAmdX3dDualCcd,
    bool HasTunableNvidiaGpu,
    IReadOnlyList<string> Reasons);

/// <summary>What the matcher recommends for a machine: the safe generic baseline plus any
/// chip-specific profiles, and the classification that produced them.</summary>
public sealed record ProfileRecommendation(
    IReadOnlyList<ChipProfile> Profiles,
    ChipClassification Classification);

/// <summary>M6 profile matching service: detected chip (M1 inventory) → recommended
/// profile(s). Conservative + fail-closed: only proposes what is safe/sourced for the
/// detected family; an unknown/odd chip gets the generic safe baseline, never a guess.</summary>
public interface IChipProfileMatcher
{
    Result<ProfileRecommendation> Recommend(HardwareInventory hw);
}

// -----------------------------------------------------------------------
// M6 — ChipProfileMatcher.
//
// Conservative composition (cahier §10/§16, « jamais inventer »):
//   * The generic safe baseline is always recommended (the floor).
//   * The AMD dual-CCD X3D profile is added IFF the CPU is confidently that family.
//   * The NVIDIA Option-A profile is added IFF a tunable discrete desktop GeForce GPU
//     is present.
// An unrecognized chip adds nothing chip-specific → generic only. A null/missing
// inventory is fail-closed (PROFILE.INPUT_INVALID) — we never fabricate a profile.
// Each tweak's own AppliesTo (current SettingsState gating) is the FINAL safety gate at
// apply time; the matcher's job is conservative chip-level ELIGIBILITY, nothing more.
// Pure over the catalog; never throws.
// -----------------------------------------------------------------------
public sealed class ChipProfileMatcher : IChipProfileMatcher
{
    private readonly IChipProfileCatalog _catalog;

    public ChipProfileMatcher(IChipProfileCatalog catalog) => _catalog = catalog;

    public Result<ProfileRecommendation> Recommend(HardwareInventory hw)
    {
        // Fail-closed: an unusable inventory never yields a guessed profile.
        if (hw is null || hw.Cpu is null)
            return Result<ProfileRecommendation>.Fail(ErrorCodes.ProfileInputInvalid,
                "HardwareInventory (and its Cpu) is required to recommend a profile.");

        var profiles = new List<ChipProfile> { _catalog.Generic };
        var reasons = new List<string>();

        var isX3d = ChipClassifier.IsAmdX3dDualCcd(hw.Cpu);
        if (isX3d)
        {
            var p = _catalog.ByFamily(ChipFamily.AmdX3dDualCcd);
            if (p.IsOk && p.Value is not null)
            {
                profiles.Add(p.Value);
                reasons.Add($"AMD dual-CCD X3D detected ('{hw.Cpu.Model}', {hw.Cpu.CcdCount} CCD) → CCD-routing repair profile.");
            }
            else
            {
                // Family detected but no curated profile yet: stay generic, say so honestly.
                reasons.Add("AMD dual-CCD X3D detected but no curated profile available → generic only.");
            }
        }
        else
        {
            reasons.Add("No dual-CCD AMD X3D CPU → CCD-routing profile not applicable.");
        }

        var hasNvidia = ChipClassifier.HasTunableNvidiaDesktopGpu(hw);
        if (hasNvidia)
        {
            var p = _catalog.ByFamily(ChipFamily.NvidiaOptionA);
            if (p.IsOk && p.Value is not null)
            {
                profiles.Add(p.Value);
                reasons.Add("Tunable discrete desktop GeForce GPU detected → NVIDIA Option-A profile.");
            }
            else
            {
                reasons.Add("NVIDIA GPU detected but no curated profile available → generic only.");
            }
        }
        else
        {
            reasons.Add("No tunable discrete desktop GeForce GPU → NVIDIA Option-A profile not applicable.");
        }

        var classification = new ChipClassification(isX3d, hasNvidia, reasons);
        return Result<ProfileRecommendation>.Ok(new ProfileRecommendation(profiles, classification));
    }
}
