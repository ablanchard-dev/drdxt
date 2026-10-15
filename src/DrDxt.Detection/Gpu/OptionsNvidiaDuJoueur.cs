using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Deux options du pilote NVIDIA que DrDXT ne pose pas, parce que ce
/// sont des choix d'image et de latence du joueur, mais qu'il doit connaître : la surcouche DLSS de l'appli NVIDIA (DLSS 4,
/// génération d'images, Smooth Motion : officielle, pilotes 572 et plus) et le mode faible latence « Ultra », qui ne vaut
/// que pour les vieux jeux DX9-11 sans Reflex, limités par la carte entre 60 et 100 images par seconde. Sans chiffre
/// promis : la génération d'images augmente le compte d'images en augmentant la latence, et « Ultra » peut coûter des
/// images hors de sa fenêtre (Blur Busters, forum NVIDIA). Sources dans le guide, section 5.19.
/// </summary>
public static class OptionsNvidiaDuJoueur
{
    public const string Nom = "options NVIDIA du joueur";

    /// <summary>Pur : un constat d'information sur une machine à carte NVIDIA, rien ailleurs.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<GpuInfo> cartes)
    {
        if (!cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) yield break;

        yield return new RepairFinding("GPU DRIVER", Nom, Docteur.T("ON.Valeur"), null,
            RepairStatus.Observed,
            Docteur.T("ON.Explication"),
            RepairAction.Recommandation);
    }
}
