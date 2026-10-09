using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// M6 — CoreProfileMatcherAdapter.
//
// Implements Core's authoritative IProfileMatcher (the shape the rest of the app already
// knows) by PROJECTING the rich M6 recommendation down to a single composite Core
// Profile. This mirrors the repo pattern of keeping Core's contract untouched while the
// rich model lives in the layer project (cf. EnginePlan vs ITweakEngine). Downstream
// consumers that only need "which tweak ids do I recommend" get a clean, fix-misconfig-
// first, de-duplicated list straight off this adapter.
//
//   * Profile.Id           = composite of the matched family ids.
//   * Profile.Match        = informational CPU/GPU descriptors of what matched.
//   * RecommendedTweakIds  = union of every selection, ordered FixMisconfig → Latency →
//                            GpuTune, first occurrence wins (no duplicates).
//   * Profile.Tested       = AND over the matched profiles (false unless ALL are
//                            wave-validated) — honest: a composite is only "tested" when
//                            every part is.
//
// Fail-closed: a failed Recommend (unusable inventory) is surfaced verbatim; never a
// fabricated profile.
// -----------------------------------------------------------------------
public sealed class CoreProfileMatcherAdapter : IProfileMatcher
{
    private readonly IChipProfileMatcher _matcher;

    public CoreProfileMatcherAdapter(IChipProfileMatcher matcher) => _matcher = matcher;

    public Result<Profile> Match(HardwareInventory hw)
    {
        var rec = _matcher.Recommend(hw);
        if (!rec.IsOk || rec.Value is null)
            return Result<Profile>.Fail(rec.Error!.Value.Code, rec.Error!.Value.Message);

        var profiles = rec.Value.Profiles;

        var id = "drdxt." + string.Join("+", profiles.Select(p => p.Family.ToString().ToLowerInvariant()));

        // Union of all selections, fix-misconfig first, de-duplicated (first wins).
        var tweakIds = profiles
            .SelectMany(p => p.Selections)
            .OrderBy(s => (int)s.Intent)
            .Select(s => s.TweakId)
            .Distinct()
            .ToList();

        var cpuPattern = rec.Value.Classification.IsAmdX3dDualCcd ? "AMD * X3D (dual-CCD)" : null;
        var gpuPattern = rec.Value.Classification.HasTunableNvidiaGpu ? "NVIDIA GeForce RTX/GTX (desktop)" : null;

        // Composite is "tested" only when every matched profile is wave-validated.
        var tested = profiles.All(p => p.Tested);

        return Result<Profile>.Ok(new Profile(id, new HardwareMatch(cpuPattern, gpuPattern), tweakIds, tested));
    }
}
