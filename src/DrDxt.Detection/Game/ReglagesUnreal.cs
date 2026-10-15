using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Game;

/// <summary>Ce que le fichier de réglages d'un jeu Unreal Engine dit, et de quand il date.</summary>
public sealed record ReglagesUnrealLus(
    string Jeu, string Fichier, DateTime ModifieLe, bool? VSync, double? PlafondFps, int? ModeFenetre, string? Reflex, string? Upscaling,
    int? EclairageGlobal = null, int? Reflets = null);

/// <summary>
/// Le profil par jeu au-delà de Call of Duty, pour les jeux Unreal Engine (JeuB, par exemple). Les jeux Unreal écrivent leurs
/// réglages dans %LOCALAPPDATA%\{Projet}\Saved\Config\{WindowsClient|Windows|WindowsNoEditor}\GameUserSettings.ini, avec
/// les clés de UGameUserSettings (bUseVSync, FrameRateLimit, FullscreenMode). Relevé le 07/10 sur la machine de référence : 7 jeux Unreal,
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
            S("NvidiaReflexMode"), S("UpscalingMethod"), I("sg.GlobalIlluminationQuality"), I("sg.ReflectionQuality"));
    }

    /// <summary>Les niveaux des groupes de qualité d'Unreal (sg.*), de 0 à 4 (documentation d'Epic, « Scalability Reference »).</summary>
    private static string Niveau(int n) => n is >= 0 and <= 4 ? Docteur.T("RU.Niveau" + n) : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Épique (3) et cinématique (4) : au-delà de ce que le guide de performance de Lumen d'Epic vise pour un jeu fluide
    /// (élevé = 60 images/s sur console, épique = 30). Éclairage global et reflets règlent Lumen, qui charge la carte
    /// graphique : les baisser n'aide que si elle freine, ce que dit le goulot relevé en partie.
    /// </summary>
    private const int NiveauCouteux = 3;

    /// <summary>Le conseil sur Lumen, et s'il vaut un geste : processeur qui freine, la phrase dit seulement que baisser changera peu.</summary>
    private static (string Texte, bool Geste)? ConseilLumen(ReglagesUnrealLus r, BottleneckState? goulot)
    {
        var hauts = new List<string>();
        if (r.EclairageGlobal >= NiveauCouteux) hauts.Add(Docteur.T("RU.EclairageGlobal", Niveau(r.EclairageGlobal.Value)));
        if (r.Reflets >= NiveauCouteux) hauts.Add(Docteur.T("RU.Reflets", Niveau(r.Reflets.Value)));
        if (hauts.Count == 0) return null;
        var (cle, geste) = goulot switch
        {
            BottleneckState.GpuBound or BottleneckState.MixedGpuLeaning or BottleneckState.ThermalOrPowerThrottle => ("RU.Lumen.Carte", true),
            BottleneckState.CpuBoundStrong or BottleneckState.CpuBoundOrAppBound => ("RU.Lumen.Processeur", false),
            // Plafonnée (VSync, plafond d'images), seulement en latence ou en manque de mémoire : une partie ne départagerait
            // rien, aucun conseil.
            BottleneckState.CappedOrVsyncLimited or BottleneckState.LatencyOnly or BottleneckState.RamCommitPressure => ((string?)null, false),
            _ => ("RU.Lumen.AJuger", true),
        };
        if (cle is null) return null;
        return (Docteur.T(cle, string.Join(Docteur.T("RU.Et"), hauts), Niveau(NiveauCouteux - 1)), geste);
    }

    private static string Mode(int? m) => m switch
    {
        0 => Docteur.T("RU.PleinEcran"),
        1 => Docteur.T("RU.PleinEcranFenetre"),
        2 => Docteur.T("RU.Fenetre"),
        null => "?",
        _ => m.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Un constat « à regarder » : ce que le jeu est réglé à faire, sans verdict qu'aucune source ne fonde.</summary>
    /// <param name="goulot">Le goulot de la dernière partie relevée de ce jeu, s'il y en a une.</param>
    public static RepairFinding Constat(ReglagesUnrealLus r, DateTime maintenant, BottleneckState? goulot = null)
    {
        var vieux = maintenant - r.ModifieLe > ReglagesCod.AgeMaximal
            ? Docteur.T("RU.Vieux", r.ModifieLe)
            : "";
        var plafond = r.PlafondFps is { } p ? (p > 0 ? Docteur.T("RU.ImagesSec", p.ToString("0", CultureInfo.InvariantCulture)) : Docteur.T("RU.Aucun")) : "?";
        var vsync = r.VSync is { } s ? Docteur.T(s ? "RU.Active" : "RU.Coupe") : "?";
        var details = Docteur.T("RU.Details", vsync, plafond, Mode(r.ModeFenetre));
        if (r.Reflex is { } reflex) details += Docteur.T("RU.Reflex", reflex);
        if (r.Upscaling is { } up) details += Docteur.T("RU.Upscaling", up);
        if (r.EclairageGlobal is { } gi) details += " ; " + Docteur.T("RU.EclairageGlobal", Niveau(gi));
        if (r.Reflets is { } re) details += " ; " + Docteur.T("RU.Reflets", Niveau(re));
        var explication = Docteur.T("RU.Explication", r.Fichier, details) + vieux;
        return ConseilLumen(r, goulot) switch
        {
            { Geste: true } c => new RepairFinding(ReglagesCod.Domaine, Docteur.Nom("RU.Nom", r.Jeu), details, null, RepairStatus.Observed,
                explication, RepairAction.Recommandation, Remede: c.Texte),
            { } c => new RepairFinding(ReglagesCod.Domaine, Docteur.Nom("RU.Nom", r.Jeu), details, null, RepairStatus.Observed,
                explication + " " + c.Texte),
            null => new RepairFinding(ReglagesCod.Domaine, Docteur.Nom("RU.Nom", r.Jeu), details, null, RepairStatus.Observed, explication),
        };
    }
}
