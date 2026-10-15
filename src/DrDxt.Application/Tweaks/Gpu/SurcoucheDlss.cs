using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Le dernier modèle DLSS (mise à l'échelle seulement, jamais la génération d'images) au profil pilote d'un seul jeu : la
/// « surcouche DLSS » de l'appli NVIDIA, par deux réglages du pilote nommés dans NvApiDriverSettings.h (github.com/NVIDIA/nvapi) :
/// NGX_DLSS_SR_OVERRIDE (0x10E41E01) à 1, et NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION (0x10E41DF3) à « Latest »
/// (0x00FFFFFF). Un choix du joueur, posé sur son clic, jamais en essai : le compteur d'images ne juge pas la netteté.
/// Le profil du jeu doit exister et se lire : DrDXT n'en crée pas et n'écrit jamais au global à sa place. L'annulation
/// suit la provenance lue (reprise de la règle de PlanifierDrs) : posé par le joueur ⇒ sa valeur reécrite ; défaut,
/// hérité ou livré ⇒ le réglage supprimé du profil ; inconnue ⇒ rien n'est proposé.
/// </summary>
public static class SurcoucheDlss
{
    public const uint Override = 0x10E41E01, Preset = 0x10E41DF3, DernierModele = 0x00FFFFFF;

    public static Result<(IReadOnlyList<PrivilegedOp> Faire, IReadOnlyList<PrivilegedOp> Annuler)> Planifier(INvApi nv, string jeu)
    {
        var faire = new List<PrivilegedOp>();
        var annuler = new List<PrivilegedOp>();
        foreach (var (id, cible) in new[] { (Override, 1u), (Preset, DernierModele) })
        {
            var lu = nv.GetDrsForApplication(id, jeu);
            if (!lu.IsOk)
                return Result<(IReadOnlyList<PrivilegedOp>, IReadOnlyList<PrivilegedOp>)>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "The game's driver profile could not be read: nothing is proposed.");
            var (valeur, origine) = lu.Value;
            if (valeur == cible) continue;
            PrivilegedOp? inverse = origine.Origin switch
            {
                // Une valeur du joueur que la liste blanche refuserait de rejouer : l'Annuler serait refusé, rien n'est proposé.
                DrsValueOrigin.SetByUser when ValeursDrs.Documentee(id, valeur) => new ApplyNvApiDrsSettingOp(id, valeur, jeu),
                DrsValueOrigin.SetByUser => null,
                DrsValueOrigin.Unknown => null,
                var o => new DeleteNvApiDrsSettingOp(id, jeu, o, valeur),
            };
            if (inverse is null)
                return Result<(IReadOnlyList<PrivilegedOp>, IReadOnlyList<PrivilegedOp>)>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "The origin of the game's DLSS setting is unknown: nothing is proposed.");
            faire.Add(new ApplyNvApiDrsSettingOp(id, cible, jeu));
            annuler.Add(inverse);
        }
        return faire.Count == 0
            ? Result<(IReadOnlyList<PrivilegedOp>, IReadOnlyList<PrivilegedOp>)>.Fail(ErrorCodes.TweakNotApplicable, "Already on the latest DLSS model.")
            : Result<(IReadOnlyList<PrivilegedOp>, IReadOnlyList<PrivilegedOp>)>.Ok((faire, annuler));
    }
}
