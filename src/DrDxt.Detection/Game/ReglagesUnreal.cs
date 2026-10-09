using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Game;

/// <summary>Ce que le fichier de réglages d'un jeu Unreal Engine dit, et de quand il date.</summary>
public sealed record ReglagesUnrealLus(
    string Jeu, string Fichier, DateTime ModifieLe, bool? VSync, double? PlafondFps, int? ModeFenetre, string? Reflex, string? Upscaling);

/// <summary>
/// 07/10/2026 (chantier P1, enquête n° 11) : le profil par jeu au-delà de Call of Duty. Le plan disait « à rouvrir
/// quand un autre jeu apparaît dans les parties » : JeuB (Unreal Engine) y est. Les jeux Unreal écrivent leurs
/// réglages dans %LOCALAPPDATA%\{Projet}\Saved\Config\{WindowsClient|Windows|WindowsNoEditor}\GameUserSettings.ini, avec
/// les clés de UGameUserSettings (bUseVSync, FrameRateLimit, FullscreenMode). Relevé le 07/10 sur le PC du mainteneur : 7 jeux Unreal,
/// dont JeuB. Lu, jamais écrit (même règle que Call of Duty : un jeu protégé par un anti-triche réécrit ce fichier).
/// FullscreenMode suit EWindowMode d'Unreal Engine (documentation d'Epic) : 0 plein écran, 1 plein écran fenêtré, 2
/// fenêtré. Reflex et upscaling sont des clés propres à chaque jeu : leur valeur est citée brute, jamais interprétée.
/// </summary>
public static class ReglagesUnreal
{
    private static readonly Regex ExeUnreal = new(@"^(?<p>[A-Za-z0-9_]+?)(Client)?-Win64-Shipping\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Plateformes = ["WindowsClient", "Windows", "WindowsNoEditor"];

    /// <summary>Le nom affiché du jeu : le projet, sinon l'exécutable sans extension.</summary>
    public static string Nom(string exe) => Projet(exe) ?? Path.GetFileNameWithoutExtension(exe);

    /// <summary>Le nom du projet Unreal d'après l'exécutable du jeu (« JeuBClient-Win64-Shipping.exe » ⇒ « JeuB »),
    /// ou <c>null</c> si l'exécutable n'a pas la forme d'un jeu Unreal livré.</summary>
    internal static string? Projet(string exe) => ExeUnreal.Match(Path.GetFileName(exe)) is { Success: true } m ? m.Groups["p"].Value : null;

    /// <summary>Le fichier de réglages le plus récent du jeu, ou <c>null</c>. Fortnite range ses réglages sous
    /// « FortniteGame » : le projet suivi de « Game » est essayé aussi.</summary>
    public static FileInfo? Trouver(string exe, string localAppData)
    {
        if (Projet(exe) is not { } projet) return null;
        return new[] { projet, projet + "Game" }
            .SelectMany(p => Plateformes.Select(pl => Path.Combine(localAppData, p, "Saved", "Config", pl, "GameUserSettings.ini")))
            .Where(File.Exists).Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
    }

    public static ReglagesUnrealLus Lire(string jeu, string fichier, string texte, DateTime modifieLe)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brute in texte.Split('\n'))
        {
            var ligne = brute.Trim();
            var egal = ligne.IndexOf('=');
            if (egal <= 0 || ligne.StartsWith(';') || ligne.StartsWith('[')) continue;
            v.TryAdd(ligne[..egal].Trim(), ligne[(egal + 1)..].Trim());
        }
        string? S(string k) => v.TryGetValue(k, out var x) && x.Length > 0 ? x : null;
        bool? B(string k) => S(k) is { } x && bool.TryParse(x, out var b) ? b : null;
        double? D(string k) => double.TryParse(S(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        int? I(string k) => int.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
        return new ReglagesUnrealLus(jeu, fichier, modifieLe, B("bUseVSync"), D("FrameRateLimit"), I("FullscreenMode"),
            S("NvidiaReflexMode"), S("UpscalingMethod"));
    }

    private static string Mode(int? m) => m switch
    {
        0 => "plein écran",
        1 => "plein écran fenêtré",
        2 => "fenêtré",
        null => "?",
        _ => m.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Un constat « à regarder » : ce que le jeu est réglé à faire, sans verdict qu'aucune source ne fonde.</summary>
    public static RepairFinding Constat(ReglagesUnrealLus r, DateTime maintenant)
    {
        var vieux = maintenant - r.ModifieLe > ReglagesCod.AgeMaximal
            ? $" (fichier du {r.ModifieLe:dd/MM/yyyy} : si le jeu a changé de version depuis, ces valeurs sont peut-être dépassées)"
            : "";
        var plafond = r.PlafondFps is { } p ? (p > 0 ? p.ToString("0", CultureInfo.InvariantCulture) + " images/s" : "aucun") : "?";
        var vsync = r.VSync is { } s ? (s ? "activé" : "coupé") : "?";
        var details = $"V-Sync {vsync} ; plafond d'images {plafond} ; affichage {Mode(r.ModeFenetre)}";
        if (r.Reflex is { } reflex) details += $" ; Reflex « {reflex} » (valeur du jeu)";
        if (r.Upscaling is { } up) details += $" ; mise à l'échelle {up}";
        return new RepairFinding(ReglagesCod.Domaine, $"réglages de {r.Jeu}", details, null, RepairStatus.Observed,
            $"Lu dans le fichier de réglages du jeu ({r.Fichier}) : {details}. Les modes d'affichage suivent ceux d'Unreal "
            + "Engine (documentation d'Epic). DrDXT n'écrit rien dans les fichiers du jeu." + vieux);
    }
}
