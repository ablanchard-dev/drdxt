using DrDxt.Core;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.Detection;

/// <summary>
/// La telemetrie materielle du produit, lue par NVML — la meme bibliotheque que `nvidia-smi`.
///
/// Quatre grandeurs, toutes par des exports NVML PUBLICS et documentes, sans elevation :
/// puissance, temperature, frequence graphique, raisons de bridage.
///
/// Ce commentaire disait le 07/09/2026 : « la frequence coeur et la raison de bridage
/// demanderaient NVAPI et ses identifiants prives ». C'etait faux. Verifie dans la doc officielle
/// le 08/09 : `nvmlDeviceGetClockInfo` et `nvmlDeviceGetCurrentClocksEventReasons` sont des
/// exports nommes, documentes et stables. La gate etait une hypothese que je n'avais pas
/// verifiee, et elle privait le root-cause de sa grandeur la plus directe.
/// Source : docs.nvidia.com/deploy/nvml-api, groupes nvmlDeviceQueries et nvmlClocksEventReasons.
///
/// Sur les cartes Ada grand public (RTX 40), la lecture instantanee de puissance est N/A ;
/// `TryGetPowerUsageMilliwatts` retombe deja sur le tampon d'echantillons. Ici on se contente de
/// traduire « pas lisible » en <c>null</c>, jamais en zero.
/// </summary>
public sealed class NvmlHardwareProbe : IHardwareProbe
{
    private readonly INvmlNative _nvml;
    private readonly int _gpu;

    public NvmlHardwareProbe(INvmlNative? nvml = null, int gpu = 0)
    {
        _nvml = nvml ?? new NvmlNative();
        _gpu = gpu;
    }

    public HardwareReading Lire()
    {
        if (!_nvml.IsAvailable) return new HardwareReading(null, null);

        double? watts = _nvml.TryGetPowerUsageMilliwatts(_gpu, out var mw) ? mw / 1000.0 : null;
        double? degres = _nvml.TryGetTemperatureCelsius(_gpu, out var c) ? c : null;
        double? mhz = _nvml.TryGetClockMhz(_gpu, out var f) ? f : null;

        // Le booleen decide, pas la valeur : `nvmlClocksEventReasonNone` vaut 0x0, donc un
        // masque nul lu avec succes signifie « rien ne bride » et doit etre conserve. Tester
        // « != 0 » ici effacerait exactement l'information la plus rassurante.
        ClocksEventReason? raisons = _nvml.TryGetClocksEventReasons(_gpu, out var bits)
            ? (ClocksEventReason)bits
            : null;

        return new HardwareReading(watts, degres, mhz, raisons);
    }
}
