using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>Adaptateur graphique vu par DXGI (VRAM fiable, pas Win32_VideoController qui wrap à 4 Go).</summary>
/// <param name="Luid">04/10/2026 : l'identifiant de la carte au format des compteurs Windows (« luid_0x…_0x… ») ;
/// <c>null</c> quand il n'est pas lu.</param>
public sealed record DxgiAdapter(string Description, int VendorId, long DedicatedVramBytes, string? Luid = null);

public interface IDxgiProvider
{
    Result<IReadOnlyList<DxgiAdapter>> EnumerateAdapters();
}
