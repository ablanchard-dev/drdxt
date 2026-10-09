using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Infers Resizable BAR state from the NVML BAR1 window size relative to the framebuffer.
///
/// In practice the BAR1 window is one of two things: the legacy ~256 MiB aperture (ReBAR OFF)
/// or (close to) the full VRAM size (ReBAR ON) — there is no meaningful in-between. So the
/// midpoint of the framebuffer cleanly classifies the two: BAR1 ≥ FB/2 ⇒ On, else Off.
/// Source: nvmlDeviceGetBAR1MemoryInfo semantics (docs.nvidia.com/deploy/nvml-api), the value
/// nvidia-smi reports as "BAR1 Memory Usage / Total".
/// </summary>
public static class ReBarHeuristic
{
    public static TriState FromBar1AndFb(ulong bar1TotalBytes, ulong fbTotalBytes)
    {
        if (bar1TotalBytes == 0 || fbTotalBytes == 0) return TriState.Unknown;
        return bar1TotalBytes >= fbTotalBytes / 2 ? TriState.On : TriState.Off;
    }
}
