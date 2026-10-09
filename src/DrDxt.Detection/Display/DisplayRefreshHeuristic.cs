using DrDxt.Core;

namespace DrDxt.Detection.Display;

// RefreshHeadroom a DEMENAGE dans DrDxt.Core (DetectionContracts) le 02/09/2026 : SettingsState
// le porte desormais, et un type de l'assembly de detection ne peut pas etre reference depuis
// Core sans inverser la dependance. Le type est identique, seul son foyer a change.

/// <summary>
/// Decides whether the machine is running a display below the refresh rate its panel
/// supports — the cheapest free win on a gaming PC, and one no other lever compensates:
/// a 200 Hz panel driven at 143 Hz throws away 28% of the frames the GPU already renders.
///
/// Two rules make this honest rather than merely plausible:
///
/// 1. <b>Same resolution only.</b> The caller must pass the modes available AT the active
///    resolution. A panel that offers 200 Hz at 1280x1024 and 144 Hz at 1920x1080 is
///    correctly configured at 144; recommending 200 there would silently trade resolution
///    for frequency, which is a user decision, not an optimisation.
///
/// 2. <b>A 1 Hz gap is not an opportunity.</b> Windows reports a 144 Hz mode as 143
///    (the real timing is ~143.98 and the API truncates). Treating that as headroom would
///    make DrDXT nag about a display that is already maxed out.
/// </summary>
public static class DisplayRefreshHeuristic
{
    /// <summary>Gap below which two modes are the same mode. See rule 2 above.</summary>
    public const int ToleranceHz = 1;

    public static RefreshHeadroom Evaluate(int currentHz, IReadOnlyList<int>? availableHzAtSameResolution)
    {
        if (currentHz <= 0 || availableHzAtSameResolution is null || availableHzAtSameResolution.Count == 0)
        {
            return new RefreshHeadroom(TriState.Unknown, currentHz, 0);
        }

        var best = currentHz;
        foreach (var hz in availableHzAtSameResolution)
        {
            if (hz > best) best = hz;
        }

        var headroom = best - currentHz > ToleranceHz ? TriState.On : TriState.Off;
        return new RefreshHeadroom(headroom, currentHz, best);
    }
}
