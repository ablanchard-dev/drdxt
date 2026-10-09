using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// Answers "is my frame cap the right one for the screen I actually play on?".
///
/// Found on the author's machine on 02/09/2026: the driver cap sat at 141 while the primary
/// display ran at 200 Hz. 141 is 144 - 3 and 144 is the SECOND screen — the low-latency rule had
/// been computed on the wrong panel, costing frames every session, and the product had been
/// READING that 141 for months as a revert value without ever saying a word about it.
///
/// The rule is a RELATION, never a magic number: on a machine whose primary is 144 Hz the same
/// 141 is correct. So the verdict is derived from the primary's refresh and, only when that fails
/// to explain the value, from the other attached displays.
///
/// Pure and total: no I/O, no clock. A null cap or an unusable display list degrades to
/// <see cref="FrameCapVerdict.Indeterminate"/> — it never invents a clean bill of health.
/// </summary>
public static class FrameCapInferencer
{
    /// <summary>The driver's low-latency rule: cap 3 fps under the refresh so frames stay inside
    /// the VRR window. Same constant the VRR tweak writes.</summary>
    private const int CapBelowRefresh = 3;

    /// <param name="frameCapFps">FRL_FPS as read from the driver; 0 is the driver's "no limit",
    /// null means it could not be read.</param>
    /// <param name="displays">Attached displays. Empty or without a primary → Indeterminate.</param>
    /// <param name="origin">Optional: who set the value, as the driver reports it. Carried
    /// through untouched — it tells the reader what to DO about the cap (correct it yourself vs
    /// a value NVIDIA ships for this game), and it must never influence the verdict.</param>
    public static FrameCapState Infer(int? frameCapFps, IReadOnlyList<DisplayInfo>? displays,
        DrsOriginState? origin = null)
    {
        if (frameCapFps is null)
            return new(FrameCapVerdict.Indeterminate,
                "The driver frame cap could not be read, so nothing is claimed about it.", origin);

        var primary = displays?.FirstOrDefault(d => d.IsPrimaryGameDisplay);
        if (primary is null)
            return new(FrameCapVerdict.Indeterminate,
                "No primary display was enumerated, so there is nothing to compare the cap against.", origin);

        // A refresh of 0 means "not read" (see DisplayInfo): comparing against it would manufacture
        // a verdict out of a missing reading.
        if (primary.RefreshHz <= 0)
            return new(FrameCapVerdict.Indeterminate,
                "The primary display did not report a refresh rate, so the cap cannot be judged.", origin);

        var cap = frameCapFps.Value;
        if (cap == 0 || cap >= primary.RefreshHz)
            return new(FrameCapVerdict.NotCapping,
                $"The frame cap ({(cap == 0 ? "off" : cap + " fps")}) never binds on a " +
                $"{primary.RefreshHz} Hz screen.");

        if (cap == primary.RefreshHz - CapBelowRefresh)
            return new(FrameCapVerdict.Coherent,
                $"The cap ({cap} fps) is the low-latency value for your {primary.RefreshHz} Hz screen.", origin);

        var other = displays!.FirstOrDefault(d =>
            !d.IsPrimaryGameDisplay && d.RefreshHz > 0 && cap == d.RefreshHz - CapBelowRefresh);

        if (other is not null)
            return new(FrameCapVerdict.ComputedOnAnotherDisplay,
                $"The cap ({cap} fps) is the low-latency value for your {other.RefreshHz} Hz " +
                $"screen, but you play on the {primary.RefreshHz} Hz one: it was computed on the " +
                $"wrong display and is costing you frames.", origin);

        return new(FrameCapVerdict.CapsBelowPrimary,
            $"The cap ({cap} fps) is below what your {primary.RefreshHz} Hz screen can show. " +
            "No attached display explains that value, so its origin is not established here.", origin);
    }
}
