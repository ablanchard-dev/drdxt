using System.Runtime.InteropServices;


namespace DrDxt.Detection.Gpu.Adlx;

/// <summary>Métriques ADLX : température, fréquence et puissance lues pendant la mesure.</summary>
public sealed partial class AdlxNative
{
    // ── LireMetriques (lecture seule) ─────────────────────────────────────
    // IADLXPerformanceMonitoringServices.GetCurrentGPUMetrics sur la carte dédiée, puis chaque grandeur à part : une entrée
    // en erreur laisse la sienne à null. La puissance est celle de la carte entière (GPUTotalBoardPower, ce que NVML rend
    // sur NVIDIA) ; à défaut, celle de la puce (GPUPower), plus basse mais du même ordre. Une valeur hors de sa plage
    // possible n'est pas une mesure.
    public AdlxMetriques LireMetriques()
    {
        if (!IsAvailable)
            return AdlxMetriques.RienLu(_initDetail ?? "ADLXInitialize did not succeed.");

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, pPm = IntPtr.Zero, pMetrics = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return AdlxMetriques.RienLu(raison);
            var getPm = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetPerformanceMonitoringServices);
            if (getPm == null || getPm(_pSystem, out pPm) != 0 || pPm == IntPtr.Zero)
                return AdlxMetriques.RienLu("IADLXPerformanceMonitoringServices absent.");
            var getMetrics = Slot<GetManualDelegate>(pPm, AdlxVtableSlots.Perf_GetCurrentGpuMetrics);
            if (getMetrics == null || getMetrics(pPm, pGpu, out pMetrics) != 0 || pMetrics == IntPtr.Zero)
                return AdlxMetriques.RienLu("GetCurrentGPUMetrics failed.");

            double? Reel(int slot, double max)
                => Slot<GetDoubleDelegate>(pMetrics, slot) is { } get && get(pMetrics, out var v) == 0 && v > 0 && v < max ? v : null;
            int? Entier(int slot, int min, int max)
                => Slot<GetIntDelegate>(pMetrics, slot) is { } get && get(pMetrics, out var v) == 0 && v >= min && v < max ? v : null;

            return new AdlxMetriques(
                PuissanceW: Reel(AdlxVtableSlots.Metrics_GpuTotalBoardPower, 2000) ?? Reel(AdlxVtableSlots.Metrics_GpuPower, 2000),
                TemperatureC: Reel(AdlxVtableSlots.Metrics_GpuTemperature, 150),
                PointChaudC: Reel(AdlxVtableSlots.Metrics_GpuHotspotTemperature, 150),
                FrequenceMhz: Entier(AdlxVtableSlots.Metrics_GpuClockSpeed, 1, 10_000),
                VentilateurTrMin: Entier(AdlxVtableSlots.Metrics_GpuFanSpeed, 0, 20_000));
        }
        catch (Exception ex) { return AdlxMetriques.RienLu($"ADLX metrics read interrupted: {ex.GetType().Name}."); }
        finally
        {
            Release(pMetrics);
            Release(pPm);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // Température : IADLXPerformanceMonitoringServices.GetCurrentGPUMetrics → IADLXGPUMetrics.GPUTemperature.
    // Une valeur hors de (0, 150) °C n'est pas une température : non lue, surtout pas zéro.
    private double? LireTemperature(IntPtr pGpu)
    {
        IntPtr pPm = IntPtr.Zero, pMetrics = IntPtr.Zero;
        try
        {
            var getPm = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetPerformanceMonitoringServices);
            if (getPm == null || getPm(_pSystem, out pPm) != 0 || pPm == IntPtr.Zero) return null;
            var getMetrics = Slot<GetManualDelegate>(pPm, AdlxVtableSlots.Perf_GetCurrentGpuMetrics);
            if (getMetrics == null || getMetrics(pPm, pGpu, out pMetrics) != 0 || pMetrics == IntPtr.Zero) return null;
            var temp = Slot<GetDoubleDelegate>(pMetrics, AdlxVtableSlots.Metrics_GpuTemperature);
            return temp != null && temp(pMetrics, out var t) == 0 && t > 0 && t < 150 ? t : null;
        }
        catch { return null; }
        finally { Release(pMetrics); Release(pPm); }
    }

    // (pThis, out adlx_double) : IADLXGPUMetrics.GPUTemperature
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDoubleDelegate(IntPtr pThis, out double val);
}
