using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.Detection;

/// <summary>
/// La télémétrie de la carte en partie sur une Radeon, lue par ADLX (IADLXGPUMetrics) : ce que NVML rend sur une GeForce
/// (puissance de la carte, température, fréquence graphique), plus le point chaud et le ventilateur. Lecture seule, sans
/// élévation. ADLX ne publie pas de raisons de bridage : elles restent non lues, jamais « rien ne bride ».
/// </summary>
public sealed class AdlxHardwareProbe(IAdlxNative? adlx = null) : IHardwareProbe
{
    private readonly IAdlxNative _adlx = adlx ?? new AdlxNative();

    public HardwareReading Lire()
    {
        try
        {
            var m = _adlx.LireMetriques();
            return new HardwareReading(m.PuissanceW, m.TemperatureC, m.FrequenceMhz, Raisons: null, m.PointChaudC, m.VentilateurTrMin);
        }
        catch (Exception) { return new HardwareReading(null, null); }
    }
}

/// <summary>La sonde de la carte en partie, selon la marque de la carte dédiée : une Radeon par ADLX, le reste par NVML.
/// Sans carte lue (le relevé de fond avant tout scan), la bibliothèque qui répond choisit : NVML, puis ADLX. Une instance
/// par marque pour tout le processus : chaque bibliothèque ne s'initialise qu'une fois.</summary>
public static class SondeDeLaCarte
{
    private static readonly Lazy<AdlxHardwareProbe> Adlx = new(() => new AdlxHardwareProbe());
    private static readonly Lazy<NvmlHardwareProbe> Nvml = new(() => new NvmlHardwareProbe());
    private static readonly Lazy<bool> NvmlRepond = new(() => new DrDxt.GpuNative.Nvml.NvmlNative().IsAvailable);

    public static IHardwareProbe Pour(GpuInfo? carteDediee)
        => Pour(carteDediee, () => NvmlRepond.Value, AdlxReglages.DllPresente);

    internal static IHardwareProbe Pour(GpuInfo? carteDediee, Func<bool> nvmlRepond, Func<bool> adlxPresent)
        => carteDediee is not null ? (CartesGraphiques.EstAmd(carteDediee.Vendor) ? Adlx.Value : Nvml.Value)
            : !nvmlRepond() && adlxPresent() ? Adlx.Value : Nvml.Value;
}
