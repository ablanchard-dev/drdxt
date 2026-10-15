using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

public interface INvReBarProvider
{
    /// <summary>État du Resizable BAR NVIDIA, déduit de la taille BAR1 de NVML contre celle du framebuffer.
    /// NVML absent / pas de GPU NVIDIA / illisible → Unknown (ne lève jamais, jamais Off à l'aveugle).</summary>
    TriState GetReBarState();
}
