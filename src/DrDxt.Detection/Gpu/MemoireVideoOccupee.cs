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

    /// <summary>Pur : la mémoire vidéo dédiée par processus, sommée sur ses instances (« pid_1234_luid_…_phys_0 » du
    /// compteur « GPU Process Memory\Dedicated Usage »), les plus gros d'abord.</summary>
    public static IReadOnlyList<(int Pid, long Octets)> ParPid(IEnumerable<(string Instance, long Occupe)> compteurs)
        => compteurs.Select(c => (Pid: Pid(c.Instance), c.Occupe)).Where(c => c.Pid is not null)
            .GroupBy(c => c.Pid!.Value).Select(g => (g.Key, g.Sum(c => c.Occupe))).OrderByDescending(c => c.Item2).ToList();

    private static int? Pid(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return null;
        var fin = instance.IndexOf('_', 4);
        return fin > 4 && int.TryParse(instance.AsSpan(4, fin - 4), out var pid) ? pid : null;
    }

    /// <summary>Les processus qui tiennent de la mémoire vidéo dédiée maintenant, nommés ; <c>null</c> = compteur absent.</summary>
    public static IReadOnlyList<(string Nom, long Octets)>? ParProcessus()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Process Memory")) return null;
            var lot = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            if (!lot.Contains("Dedicated Usage")) return null;
            return ParPid(lot["Dedicated Usage"].Values.Cast<InstanceData>().Select(i => (i.InstanceName, i.RawValue)))
                .Select(p =>
                {
                    try { using var proc = Process.GetProcessById(p.Pid); return (proc.ProcessName, p.Octets); }
                    catch (Exception) { return ($"pid {p.Pid}", p.Octets); }
                }).ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Pur : la mémoire hors carte (« Non Local Usage », en mémoire système) d'un processus sur les cartes dédiées, en octets,
    /// sommée sur ses instances « pid_…_luid_…_phys_N » rattachées à chaque carte par son identifiant ; plusieurs cartes
    /// acceptées, une puce intégrée ne compte pas (tout y est en mémoire système). null = aucune instance sur une carte dédiée.
    /// Relevé sur la machine de référence le 17/10/2026 : instances « pid_2232_luid_0x00000000_0x0221C866_phys_0 », même
    /// identifiant que la carte vue par DisplayConfig.
    /// </summary>
    public static long? HorsCarte(int pid, IReadOnlyList<(string Instance, long Octets)> compteurs, IReadOnlyList<DxgiAdapter> cartes)
    {
        long? total = null;
        foreach (var carte in cartes.Where(c => c.Luid is not null && c.DedicatedVramBytes >= CarteDedieeMin))
            foreach (var c in compteurs.Where(c => c.Instance.StartsWith($"pid_{pid}_{carte.Luid}_", StringComparison.OrdinalIgnoreCase)))
                total = (total ?? 0) + c.Octets;
        return total;
    }

    /// <summary>La mémoire hors carte du processus maintenant, en Mo ; null = compteur, carte ou processus absents.</summary>
    public static double? HorsCarteMo(int pid)
    {
        try
        {
            if (_cartes is null && new VorticeDxgiProvider().EnumerateAdapters() is { IsOk: true, Value: { Count: > 0 } lues })
                _cartes = lues;
            if (_cartes is null || !PerformanceCounterCategory.Exists("GPU Process Memory")) return null;
            var lot = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            if (!lot.Contains("Non Local Usage")) return null;
            return HorsCarte(pid, lot["Non Local Usage"].Values.Cast<InstanceData>().Select(i => (i.InstanceName, i.RawValue)).ToList(), _cartes)
                is { } o ? o / (1024.0 * 1024) : null;
        }
        catch (Exception) { return null; }
    }

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
