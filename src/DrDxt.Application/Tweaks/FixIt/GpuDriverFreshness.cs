using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// GpuDriverFreshness — conservative, pure heuristic for F2.
//
// Decides whether a GPU's driver is "stale" (very old) or shows a corruption
// marker, from the free-form GpuInfo.DriverVersion string. This is intentionally
// CONSERVATIVE: a false positive only PROMPTS a guided DDU (never an auto write),
// so we err toward not flagging a borderline-recent driver.
//
//   * Corruption marker = empty / whitespace / non-numeric (e.g. "unknown") version
//     → flag (the version couldn't be read, treat as broken).
//   * Very old = the leading driver branch is below a conservative per-vendor floor.
//     - NVIDIA versions look like "551.86" → branch = the first integer group (551).
//       Floor = 500 (≈ 2023). Below it is genuinely old.
//     - AMD versions look like "24.x" / "31.0.x" → first group is the year-ish branch;
//       a tiny first group (< 20) on a non-NVIDIA driver is treated as old.
//   * Anything we can parse and that is at/above the floor → not stale.
//
// The real proof of any gain is the engine re-bench; here we only gate the prompt.
// -----------------------------------------------------------------------
internal static class GpuDriverFreshness
{
    // NVIDIA branch floor: 500-series (~late 2022/2023). Below = old.
    private const int NvidiaBranchFloor = 500;
    // Non-NVIDIA (AMD/Intel) first-group floor for the "tiny old branch" case.
    private const int OtherBranchFloor = 20;

    /// <summary>True if this GPU's driver is very old or shows a corruption marker.</summary>
    public static bool IsStaleOrCorrupt(GpuInfo gpu)
    {
        var version = gpu.DriverVersion;

        // Corruption marker: nothing usable to read.
        if (string.IsNullOrWhiteSpace(version))
            return true;

        // WDDM 4-group form ("32.0.16.1088") is what Win32_VideoController.DriverVersion actually
        // returns — the source GpuDetector reads. Its leading group is the WDDM/DirectX version,
        // Not the vendor driver branch, so it carries no freshness signal at all. Judging it
        // against the NVIDIA floor marked every NVIDIA machine stale. Refuse to judge rather than
        // fabricate a deficit; a real branch needs NVML/NVAPI, and wiring that retires this case.
        if (IsWddmQuad(version))
            return false;

        var branch = LeadingBranch(version);
        if (branch is null)
            return true; // non-numeric / unparseable = corruption marker.

        var isNvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        var floor = isNvidia ? NvidiaBranchFloor : OtherBranchFloor;
        return branch.Value < floor;
    }

    // "a.b.c.d" with four integer groups = the WDDM driver version reported by WMI.
    private static bool IsWddmQuad(string version)
    {
        var parts = version.Trim().Split('.');
        return parts.Length == 4
            && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    // The first integer group of a dotted version ("551.86" → 551, "31.0.15" → 31).
    // Returns null when there is no leading integer group (corruption marker).
    private static int? LeadingBranch(string version)
    {
        var trimmed = version.Trim();
        var firstDot = trimmed.IndexOf('.');
        var head = firstDot >= 0 ? trimmed[..firstDot] : trimmed;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }
}
