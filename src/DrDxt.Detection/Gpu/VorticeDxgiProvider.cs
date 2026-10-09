using DrDxt.Core;
using Vortice.DXGI;

namespace DrDxt.Detection.Gpu;

public sealed class VorticeDxgiProvider : IDxgiProvider
{
    public Result<IReadOnlyList<DxgiAdapter>> EnumerateAdapters()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory is null)
                return Result<IReadOnlyList<DxgiAdapter>>.Fail(ErrorCodes.DxgiFail, "CreateDXGIFactory1 returned null.");

            var adapters = new List<DxgiAdapter>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    // Ignorer l'adaptateur logiciel (WARP / Basic Render) via le flag Software.
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    adapters.Add(new DxgiAdapter(
                        d.Description?.Trim() ?? "Unknown GPU",
                        (int)d.VendorId,
                        (long)(ulong)d.DedicatedVideoMemory,
                        $"luid_0x{(uint)d.Luid.HighPart:x8}_0x{d.Luid.LowPart:x8}"));
                }
            }

            // Si rien que du software, on renvoie au moins une entrée plutôt qu'une liste vide.
            return Result<IReadOnlyList<DxgiAdapter>>.Ok(adapters.Count > 0
                ? adapters
                : new List<DxgiAdapter> { new("Software Adapter", 0, 0) });
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DxgiAdapter>>.Fail(ErrorCodes.DxgiFail, ex.Message);
        }
    }
}
