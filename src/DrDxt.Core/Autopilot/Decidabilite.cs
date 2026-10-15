using static DrDxt.Core.TexteFrancais;
namespace DrDxt.Core;

/// <summary>
/// Un candidat ne se tente que si la scene peut voir son effet.
///
/// Tenter un levier a +2 % sur une scene qui resout +/-5 % depense quatre minutes de jeu pour
/// un « aucun gain » faux : il n'y avait pas de gain detectable, ce qui n'est pas la meme chose.
/// La resolution est mesuree par l'A/A en tete de session ; cette regle la confronte a l'effet
/// attendu du candidat avant la moindre ecriture.
///
/// Le nombre d'alternances est pre-enregistre : il depend de l'effet declare, jamais du
/// resultat mesure. C'est ce qui le distingue d'un arret optionnel
/// (`FixedProtocolNoOptionalStoppingTests`).
///
/// Pur, aucune I/O.
/// </summary>
public static class Decidabilite
{
    /// <summary>Sous cet effet, une partie reelle ne tranche pas : renvoi vers un entrainement.
    /// Non chiffre au sens statistique : c'est l'ordre de grandeur du plancher
    /// mesure en partie reelle le 07/09/2026 (moyenne +/-5,4 %, 24 alternances).</summary>
    public const double PlancherPartieReellePct = 5.0;

    /// <summary>Au-dessus, 12 alternances suffisent (bruit mesure a 12 : 3,9 % sur la moyenne).</summary>
    public const double EffetFortPct = 10.0;

    /// <summary>Le nombre d'alternances fixe d'avance pour un effet attendu. Le moteur procede par
    /// blocs de 4 : 12 et 24 sont donc des nombres de passes reels.</summary>
    public static int AlternancesPour(double effetAttenduPct) =>
        Math.Abs(effetAttenduPct) >= EffetFortPct ? 12 : 24;

    /// <summary>Le motif qui interdit de tenter ce candidat sur cette scene, ou <c>null</c>.</summary>
    public static string? NonDecidable(double effetAttenduPct, SceneResolution scene,
        SceneRepresentativity representativite)
    {
        if (representativite == SceneRepresentativity.Gameplay
            && Math.Abs(effetAttenduPct) < PlancherPartieReellePct)
            return Docteur.T("DEC.SousLePlancher", effetAttenduPct, PlancherPartieReellePct);

        // Sans trois passes la resolution vaut +inf, et le motif ecrivait « (∞ %) ».
        if (!double.IsFinite(scene.ResolutionPct))
            return Docteur.T("DEC.ResolutionInconnue");

        if (!scene.PeutDetecter(effetAttenduPct))
            return Docteur.T("DEC.SousLaResolution", effetAttenduPct, scene.ResolutionPct);

        return null;
    }
}
