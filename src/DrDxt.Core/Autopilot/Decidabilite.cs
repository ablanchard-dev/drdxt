using static DrDxt.Core.TexteFrancais;
namespace DrDxt.Core;

/// <summary>
/// PHASE 18 F5 — un candidat ne se tente que si la scene peut voir son effet.
///
/// Tenter un levier a +2 % sur une scene qui resout +/-5 % depense quatre minutes de jeu pour
/// un « aucun gain » faux : il n'y avait pas de gain DETECTABLE, ce qui n'est pas la meme chose.
/// La resolution est mesuree par l'A/A en tete de session ; cette regle la confronte a l'effet
/// attendu du candidat avant la moindre ecriture.
///
/// Le nombre d'alternances est PRE-ENREGISTRE : il depend de l'effet DECLARE, jamais du
/// resultat mesure. C'est ce qui le distingue d'un arret optionnel
/// (`FixedProtocolNoOptionalStoppingTests`).
///
/// Pur, aucune I/O.
/// </summary>
public static class Decidabilite
{
    /// <summary>Sous cet effet, une partie reelle ne tranche pas : renvoi vers un entrainement.
    /// NON CHIFFRE au sens statistique : c'est l'ordre de grandeur du plancher mesure en partie
    /// reelle le 07/09/2026 (moyenne +/-5,4 %, 24 alternances), ecrit pour etre discute.</summary>
    public const double PlancherPartieReellePct = 5.0;

    /// <summary>Au-dessus, 12 alternances suffisent (bruit mesure a 12 : 3,9 % sur la moyenne).</summary>
    public const double EffetFortPct = 10.0;

    /// <summary>Le nombre d'alternances fixe d'avance pour un effet attendu. Le moteur procede par
    /// blocs de 4 : 12 et 24 sont donc des nombres de passes REELS.</summary>
    public static int AlternancesPour(double effetAttenduPct) =>
        Math.Abs(effetAttenduPct) >= EffetFortPct ? 12 : 24;

    /// <summary>Le motif qui interdit de tenter ce candidat sur cette scene, ou <c>null</c>.</summary>
    public static string? NonDecidable(double effetAttenduPct, SceneResolution scene,
        SceneRepresentativity representativite)
    {
        if (representativite == SceneRepresentativity.Gameplay
            && Math.Abs(effetAttenduPct) < PlancherPartieReellePct)
            return F($"effet attendu {effetAttenduPct:0.#} % : sous {PlancherPartieReellePct:0} % une partie ")
                 + "réelle ne tranche pas : à tenter sur un entraînement à trajet fixe";

        // 26/09/2026 : sans trois passes la resolution vaut +inf, et le motif ecrivait « (∞ %) ».
        if (!double.IsFinite(scene.ResolutionPct))
            return "la résolution de cette scène n'est pas mesurée (moins de 3 passes) : "
                 + "aucun effet n'y serait discernable";

        if (!scene.PeutDetecter(effetAttenduPct))
            return F($"effet attendu {effetAttenduPct:0.#} % sous la résolution de cette scène ")
                 + F($"({scene.ResolutionPct:0.#} %) : le tenter dépenserait du temps de jeu pour un ")
                 + "« aucun gain » qui ne dirait rien";

        return null;
    }
}
