using DrDxt.Application.Tweaks.Hardware;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Les leviers de carte qu'un rapport de vérification archivé (tests/Shared/validations) a écrits, relus, rendus et
/// relus sur une vraie carte, rangés par architecture (le rapport est nommé par elle). Sur une autre carte de la même
/// architecture validée, l'écran dit d'où vient la preuve au lieu de « pas encore prouvé ». La preuve d'une architecture
/// ne vaut pas pour une autre. Un test compare cette table aux verdicts des rapports : elle ne s'allonge qu'avec un rapport.
/// </summary>
public static class PreuvesArchivees
{
    public static readonly IReadOnlyDictionary<GpuArchitecture, IReadOnlySet<string>> LeviersParArchitecture =
        new Dictionary<GpuArchitecture, IReadOnlySet<string>>
        {
            [GpuArchitecture.AdaOrOlder] = new HashSet<string>(StringComparer.Ordinal) { "gpu.power_limit" },
        };

    public static bool Prouve(GpuArchitecture architecture, string levier)
        => LeviersParArchitecture.TryGetValue(architecture, out var leviers) && leviers.Contains(levier);
}
