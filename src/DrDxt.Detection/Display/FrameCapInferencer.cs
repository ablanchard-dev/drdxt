using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// « Mon plafond d'images est-il le bon pour l'écran où je joue ? » Relevé sur la machine de référence le 02/09/2026 :
/// plafond du pilote à 141 pour un écran principal à 200 Hz, soit 144 - 3, calculé sur le second écran. Une relation,
/// pas un nombre : 141 est juste sous 144 Hz. Le verdict vient de l'écran principal, puis seulement des autres écrans.
/// Pur ; plafond null ou écrans inutilisables → <see cref="FrameCapVerdict.Indeterminate"/>.
/// </summary>
public static class FrameCapInferencer
{
    /// <param name="frameCapFps">FRL_FPS tel que lu au pilote ; 0 est le « pas de limite » du pilote,
    /// null veut dire qu'il n'a pas pu être lu.</param>
    /// <param name="displays">Écrans branchés. Vide ou sans écran principal → Indeterminate.</param>
    /// <param name="origin">Facultatif : qui a posé la valeur, selon le pilote. Transmis tel quel pour dire quoi en faire
    /// (le corriger, ou une valeur livrée par NVIDIA pour ce jeu) ; il n'influe jamais sur le verdict.</param>
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

        // Une fréquence de 0 veut dire « non lu » (DisplayInfo) : pas de verdict sur une lecture manquante.
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
