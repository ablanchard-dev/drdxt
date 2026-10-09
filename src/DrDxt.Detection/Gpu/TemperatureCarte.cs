using DrDxt.Detection.Gpu.Adlx;
using DrDxt.GpuNative.Igcl;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.Detection.Gpu;

/// <summary>Une température de carte lue, avec la bibliothèque du pilote qui l'a rendue ; ou rien et pourquoi.</summary>
public sealed record TemperatureCarteLue(double? Celsius, string? Source, string? Raison);

/// <summary>
/// 07/10/2026 (A5) : la température de la carte graphique, quelle que soit la marque, par la bibliothèque que le pilote
/// installe lui-même : NVML (NVIDIA), ADLX (AMD), IGCL (Intel). Ni HWiNFO ni pilote noyau ; lecture seule.
/// Un seul fabricant répond : le premier dont la bibliothèque est là (NVIDIA, puis AMD, puis Intel). Sans cet ordre
/// exclusif, une carte NVIDIA muette laisserait parler la puce intégrée d'à côté sous le nom de « la carte ».
/// Sources : NVML (NVIDIA), ADLX (https://github.com/GPUOpen-LibrariesAndSDKs/ADLX), IGCL
/// (https://github.com/intel/drivers.gpu.control-library).
/// </summary>
public static class TemperatureCarte
{
    /// <summary>Une bibliothèque de pilote : <c>null</c> = absente de ce PC (on passe au fabricant suivant) ; sinon sa
    /// lecture, valeur ou raison.</summary>
    public delegate (double? Celsius, string? Raison)? Lecteur();

    public static TemperatureCarteLue Choisir(Lecteur nvidia, Lecteur amd, Lecteur intel)
    {
        foreach (var (source, lire) in new[] { ("NVML", nvidia), ("ADLX", amd), ("IGCL", intel) })
        {
            (double? Celsius, string? Raison)? lu;
            try { lu = lire(); }
            catch (Exception ex) { lu = (null, $"lecture interrompue : {ex.GetType().Name}"); }
            if (lu is not { } l) continue;
            return l.Celsius is > 0 and < 150 and { } c
                ? new TemperatureCarteLue(c, source, null)
                : new TemperatureCarteLue(null, source, l.Raison ?? "aucune valeur rendue");
        }
        return new TemperatureCarteLue(null, null, "aucune bibliothèque de pilote graphique (NVML, ADLX, IGCL) sur ce PC");
    }

    private static readonly Lazy<NvmlNative> _nvml = new(() => new NvmlNative());
    private static readonly Lazy<IgclNative> _igcl = new(() => new IgclNative());

    /// <summary>07/10/2026 : le lecteur IGCL n'a jamais tourné sur une vraie carte Intel. Le pilote de toute carte Intel,
    /// puce intégrée comprise, installe `ControlLib.dll` : l'ouvrir partout ferait tourner chez beaucoup de joueurs un appel
    /// natif jamais vu, qu'une exception .NET ne rattrape pas s'il est mal formé. Fermé jusqu'à une vraie carte (machine
    /// exportée ou essai) ; la carte Intel reste « non lu », avec cette raison.</summary>
    public const bool IgclOuvert = false;

    public const string IgclFerme = "lecteur Intel (IGCL) pas encore vérifié sur une vraie carte Intel : non lu";

    private static (double? Celsius, string? Raison)? IntelSiOuvert()
        => !IgclNative.DllPresente() ? null : IgclOuvert ? _igcl.Value.LireTemperature() : (null, IgclFerme);

    /// <summary>La vraie lecture, sur ce PC. Ne lève jamais.</summary>
    public static TemperatureCarteLue Lire() => Choisir(
        () => _nvml.Value.IsAvailable
            ? (_nvml.Value.TryGetTemperatureCelsius(0, out var c) ? (double?)c : null, "NVML n'a pas rendu de température")
            : null,
        () =>
        {
            if (!AdlxReglages.DllPresente()) return null;
            var a = AdlxReglages.Lire();
            return (a.TemperatureC, a.Raison);
        },
        IntelSiOuvert);

    /// <summary>La lecture Intel seule, pour le docteur (qui décide lui-même de la carte à juger).</summary>
    public static (double? Celsius, string? Raison) LireIntel()
        => IntelSiOuvert() ?? (null, IgclNative.DllAbsente);
}
