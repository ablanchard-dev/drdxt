using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Quatre réglages du logiciel AMD que DrDXT ne pose pas, parce que ce sont des choix d'image et de latence du joueur,
/// mais qu'il doit connaître : Radeon Boost, Image Sharpening, Radeon Super Resolution et AMD Fluid Motion Frames, lus par
/// ADLX (lecture seule). Le pendant d'<see cref="OptionsNvidiaDuJoueur"/>. HYPR-RX n'a pas d'interface ADLX : le texte le
/// dit au lieu de le deviner. Un réglage non lu se dit non lu, jamais « désactivé ».
/// </summary>
public static class ReglagesRadeonDuJoueur
{
    // Formé par Docteur.Nom, toujours en français : la clé du journal ; l'affichage le traduit.
    public static string Nom => Docteur.Nom("RAD.Nom");

    /// <summary>Pur : un constat d'information sur une machine dont la carte dédiée est une Radeon, rien ailleurs.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<GpuInfo> cartes, AdlxReglagesLus lus)
    {
        if (cartes.FirstOrDefault(CartesGraphiques.EstDediee) is not { } carte || !CartesGraphiques.EstAmd(carte.Vendor)) yield break;

        if (lus.Raison is { } raison)
        {
            yield return new RepairFinding("GPU DRIVER", Nom, null, null, RepairStatus.Unknown, Docteur.T("RAD.NonLus", raison));
            yield break;
        }

        static string Etat(bool? actif) => Docteur.T(actif switch { true => "RAD.Actif", false => "RAD.Eteint", null => "RAD.NonLu" });
        yield return new RepairFinding("GPU DRIVER", Nom,
            Docteur.T("RAD.Valeur", Etat(lus.BoostActif), Etat(lus.ImageSharpeningActif), Etat(lus.RsrActif), Etat(lus.AfmfActif)),
            Docteur.T("RAD.Attendu"), RepairStatus.Observed, Docteur.T("RAD.Explication"), RepairAction.Recommandation);
    }
}
