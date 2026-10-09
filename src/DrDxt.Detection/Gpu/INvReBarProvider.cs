using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

public interface INvReBarProvider
{
    /// <summary>NVIDIA Resizable BAR state, inferred from NVML BAR1 size vs framebuffer size.
    /// NVML absent / no NVIDIA GPU / unreadable → Unknown (never throws, never claims Off blindly).</summary>
    TriState GetReBarState();
}
