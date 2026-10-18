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
