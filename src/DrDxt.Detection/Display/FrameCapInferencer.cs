using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// Répond à « mon plafond d'images est-il le bon pour l'écran sur lequel je joue ? ».
///
/// Relevé sur la machine de référence le 02/09/2026 : le plafond du pilote était à 141 alors que
/// l'écran principal tournait à 200 Hz. 141 = 144 - 3, et 144 est le second écran : la règle
/// basse latence avait été calculée sur le mauvais écran, et coûtait des images à chaque session.
///
/// La règle est une relation, jamais un nombre magique : sur une machine dont l'écran principal
/// est à 144 Hz, le même 141 est juste. Le verdict se déduit donc de la fréquence de l'écran
/// principal et, seulement si elle n'explique pas la valeur, des autres écrans branchés.
///
/// Pur et total : ni E/S, ni horloge. Un plafond null ou une liste d'écrans inutilisable donne
/// <see cref="FrameCapVerdict.Indeterminate"/> : jamais un certificat de bonne santé inventé.
/// </summary>
public static class FrameCapInferencer
{
    /// <param name="frameCapFps">FRL_FPS tel que lu au pilote ; 0 est le « pas de limite » du pilote,
    /// null veut dire qu'il n'a pas pu être lu.</param>
    /// <param name="displays">Écrans branchés. Vide ou sans écran principal → Indeterminate.</param>
    /// <param name="origin">Facultatif : qui a posé la valeur, selon le pilote. Transmis tel quel :
    /// il dit au lecteur quoi faire du plafond (le corriger soi-même, ou une valeur livrée par NVIDIA
    /// pour ce jeu), et il ne doit jamais influer sur le verdict.</param>
    public static FrameCapState Infer(int? frameCapFps, IReadOnlyList<DisplayInfo>? displays,
        DrsOriginState? origin = null)
    {
        if (frameCapFps is null)
            return new(FrameCapVerdict.Indeterminate,
                "The driver frame cap could not be read, so nothing is claimed about it.", origin);

        var primary = displays?.FirstOrDefault(d => d.IsPrimaryGameDisplay);
        if (primary is null)
            return new(FrameCapVerdict.Indeterminate,
                "No primary display was enumerated, so there is nothing to compare the cap against.", origin);

        // Une fréquence de 0 veut dire « non lu » (voir DisplayInfo) : comparer avec elle fabriquerait
        // un verdict à partir d'une lecture manquante.
        if (primary.RefreshHz <= 0)
            return new(FrameCapVerdict.Indeterminate,
                "The primary display did not report a refresh rate, so the cap cannot be judged.", origin);

        var cap = frameCapFps.Value;
        if (cap == 0 || cap >= primary.RefreshHz)
            return new(FrameCapVerdict.NotCapping,
                $"The frame cap ({(cap == 0 ? "off" : cap + " fps")}) never binds on a " +
                $"{primary.RefreshHz} Hz screen.", origin);

        // La règle basse latence (PlafondVrr, celle qu'écrit le levier VRR) : entre la valeur Reflex et fréquence - 3.
        if (PlafondVrr.EstBasseLatence(cap, primary.RefreshHz))
            return new(FrameCapVerdict.Coherent,
                $"The cap ({cap} fps) is the low-latency value for your {primary.RefreshHz} Hz screen.", origin);

        var other = displays!.FirstOrDefault(d =>
            !d.IsPrimaryGameDisplay && d.RefreshHz > 0 && PlafondVrr.EstBasseLatence(cap, d.RefreshHz));

        if (other is not null)
            return new(FrameCapVerdict.ComputedOnAnotherDisplay,
                $"The cap ({cap} fps) is the low-latency value for your {other.RefreshHz} Hz " +
                $"screen, but you play on the {primary.RefreshHz} Hz one: it was computed on the " +
                $"wrong display and is costing you frames.", origin);

        return new(FrameCapVerdict.CapsBelowPrimary,
            $"The cap ({cap} fps) is below what your {primary.RefreshHz} Hz screen can show. " +
            "No attached display explains that value, so its origin is not established here.", origin);
    }
}
