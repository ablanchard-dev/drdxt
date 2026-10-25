using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness.Reporting;

/// <summary>Ce que « --lire » affiche : ce que l'ADLX lit sur la carte AMD dédiée, une ligne par réglage, pour qu'un
/// testeur mette chaque valeur à côté de celle d'AMD Software. Pur : met en forme une lecture déjà faite.</summary>
internal static class LectureAdlx
{
    public static IReadOnlyList<string> Lignes(AdlxReglagesLus lus, bool dllPresente)
    {
        static string Etat(bool? v) => v switch { true => "activé", false => "désactivé", null => "non lu" };
        return
        [
            $"amdadlx64.dll dans System32 : {(dllPresente ? "présente" : "absente")}",
            lus.Raison is null ? "ADLX : carte dédiée trouvée" : $"ADLX : rien lu ({lus.Raison})",
            $"SAM (ReBAR)   : {Etat(lus.Sam)}",
            $"Anti-Lag      : {Etat(lus.AntiLag)}",
            $"FRTC          : {Etat(lus.FrtcActif)}{(lus.FrtcFps is { } f ? $", {f} images/s" : "")}",
            $"Chill         : {Etat(lus.ChillActif)}{(lus.ChillMaxFps is { } c ? $", {lus.ChillMinFps}-{c} images/s" : "")}",
            $"Limite images : {(lus.LimiteImages is { } l ? (l == 0 ? "aucune" : $"{l} images/s") : "non lue")}",
            $"Vsync         : {Vsync(lus.VsyncMode)}",
            $"Température   : {(lus.TemperatureC is { } t ? $"{t:0.0} °C" : "non lue")}",
        ];
    }

    /// <summary>La limite de puissance de chaque carte lisible et sa plage (GetPowerLimitRange), telles qu'ADLX les rend.
    /// L'unité (% du TDP, ou décalage autour de 0) n'est relevée sur aucune vraie carte : c'est cette ligne, mise à côté
    /// d'AMD Software, qui la prouvera. gpu.power_limit n'est pas proposé sur AMD d'ici là.</summary>
    public static IReadOnlyList<string> LignesPuissance(IReadOnlyList<(int Index, AdlxPowerReads Lu)> cartes)
        => cartes.Count == 0
            ? ["Puissance     : non lue"]
            : cartes.Select(c => $"Puissance (carte {c.Index}) : limite {c.Lu.PowerLimitPct}, plage {c.Lu.PowerRange.Min} à "
                                 + $"{c.Lu.PowerRange.Max}, pas {c.Lu.PowerRange.Step}").ToList();

    /// <summary>La puissance de chaque index que le produit sonde, par les seules entrées Get* d'ADLX (TryReadPower) : rien
    /// n'est écrit. Sans pilote AMD : vide.</summary>
    public static IReadOnlyList<(int Index, AdlxPowerReads Lu)> LirePuissances()
    {
        var puissances = new List<(int, AdlxPowerReads)>();
        if (AdlxReglages.DllPresente() && AdlxReglages.Partage is { } adlx)
            for (var i = 0; i < DrDxt.Application.Tweaks.Hardware.AdlxGpuProbe.MaxProbeIndices; i++)
                if (adlx.TryReadPower(i, out var lu)) puissances.Add((i, lu));
        return puissances;
    }

    // ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE, les noms d'AMD Software.
    private static string Vsync(int? mode) => mode switch
    {
        0 => "toujours coupée",
        1 => "coupée sauf avis du jeu",
        2 => "activée sauf avis du jeu",
        3 => "toujours activée",
        null => "non lue",
        _ => $"mode {mode} inconnu",
    };
}
