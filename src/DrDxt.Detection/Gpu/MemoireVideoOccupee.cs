using System.Diagnostics;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// La mémoire vidéo occupée pendant une partie, en pourcentage de la carte. Une partie jugée « cadence
/// irrégulière » (137 FPS, 1 % low 77, RTX 4060 de 8 Go) n'avait aucune piste : quand un jeu dépasse la mémoire que
/// Windows lui accorde, ses données passent en mémoire système et l'image saccade (Microsoft, documentation de
/// <c>IDXGIAdapter3::QueryVideoMemoryInfo</c>, « budget »). Lu par le compteur Windows « GPU Adapter Memory », sans
/// pilote ni droit particulier, rattaché à sa carte par son identifiant. Lecture seule. Limite : le compteur compte
/// tout ce qui occupe la carte (bureau, navigateur), pas le jeu seul.
/// </summary>
public static class MemoireVideoOccupee
{
    /// <summary>Sous 1 Go dédié, c'est une carte intégrée ; elle frôle toujours 100 % et
    /// le jeu y vit en mémoire partagée.</summary>
    public const long CarteDedieeMin = 1L << 30;

    /// <summary>Le pourcentage de la carte dédiée la plus pleine ; chaque instance du compteur (« luid_…_phys_N ») compte
    /// pour la carte de même identifiant. <c>null</c> si aucun compteur ne se rattache à une carte dédiée.</summary>
    public static double? Pourcent(IReadOnlyList<(string Instance, long Occupe)> compteurs, IReadOnlyList<DxgiAdapter> cartes)
    {
        double? plusPleine = null;
        foreach (var carte in cartes.Where(c => c.Luid is not null && c.DedicatedVramBytes >= CarteDedieeMin))
        {
            var siens = compteurs.Where(c => c.Instance.StartsWith(carte.Luid + "_", StringComparison.OrdinalIgnoreCase)).ToList();
            if (siens.Count == 0) continue;
            var pourcent = 100.0 * siens.Sum(c => c.Occupe) / carte.DedicatedVramBytes;
            if (plusPleine is null || pourcent > plusPleine) plusPleine = pourcent;
        }
        return plusPleine;
    }

    // Lus une fois par session, et seulement quand la lecture réussit (un échec, pilote qui
    // redémarre en pleine partie, n'est plus gardé jusqu'au redémarrage de l'appli).
    private static IReadOnlyList<DxgiAdapter>? _cartes;
    private static bool? _compteurPresent;

    /// <summary>Le pourcentage maintenant, ou <c>null</c> si le compteur ou la carte manque.</summary>
    public static double? Lire()
    {
        try
        {
            if (_cartes is null && new VorticeDxgiProvider().EnumerateAdapters() is { IsOk: true, Value: { Count: > 0 } lues })
                _cartes = lues;
            if (_cartes is null) return null;
            _compteurPresent ??= PerformanceCounterCategory.Exists("GPU Adapter Memory");
            if (_compteurPresent != true) return null;
            var lot = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            if (!lot.Contains("Dedicated Usage")) return null;
            var compteurs = lot["Dedicated Usage"].Values.Cast<InstanceData>().Select(i => (i.InstanceName, i.RawValue)).ToList();
            return Pourcent(compteurs, _cartes);
        }
        catch (Exception) { return null; }
    }
}
