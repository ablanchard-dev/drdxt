using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// D1 (10/10/2026, boucle « allonger le bras », R3) : deux options du pilote NVIDIA que DrDXT ne pose pas, parce que ce
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

        yield return new RepairFinding("GPU DRIVER", Nom, "à ton choix, dans l'appli NVIDIA", null,
            RepairStatus.Observed,
            "deux options que DrDXT ne pose pas, parce qu'elles changent l'image ou la latence selon ton goût. La surcouche "
            + "DLSS de l'appli NVIDIA (DLSS 4 et la génération d'images) donne plus d'images comptées, mais la génération "
            + "d'images augmente la latence : à essayer dans l'appli NVIDIA, onglet Graphismes, « Surcouche DLSS », jeu par jeu. "
            + "Le mode faible latence « Ultra » vaut pour tes vieux jeux DX9 à DX11 sans Reflex, quand la carte limite entre "
            + "60 et 100 images par seconde ; ailleurs il peut coûter des images. DrDXT pose déjà « On » (une image en file) "
            + "au profil du jeu quand c'est utile. Le guide 5.19 dit où et comment.",
            RepairAction.Recommandation);
    }
}
