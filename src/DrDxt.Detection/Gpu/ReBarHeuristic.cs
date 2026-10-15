using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Déduit l'état du Resizable BAR de la taille de la fenêtre BAR1 de NVML rapportée au framebuffer.
///
/// En pratique, la fenêtre BAR1 est l'une de deux choses : l'ancienne ouverture de ~256 Mio
/// (ReBAR OFF) ou (presque) toute la VRAM (ReBAR ON) ; il n'y a pas d'entre-deux qui ait un sens.
/// Le milieu du framebuffer sépare donc proprement les deux : BAR1 ≥ FB/2 ⇒ On, sinon Off.
/// Source : sémantique de nvmlDeviceGetBAR1MemoryInfo (docs.nvidia.com/deploy/nvml-api), la valeur
/// que nvidia-smi affiche en « BAR1 Memory Usage / Total ».
/// </summary>
public static class ReBarHeuristic
{
    public static TriState FromBar1AndFb(ulong bar1TotalBytes, ulong fbTotalBytes)
    {
        if (bar1TotalBytes == 0 || fbTotalBytes == 0) return TriState.Unknown;
        return bar1TotalBytes >= fbTotalBytes / 2 ? TriState.On : TriState.Off;
    }
}
