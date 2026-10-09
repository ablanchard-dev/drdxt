using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Detection.Game;

/// <summary>Ce que le fichier de reglages du jeu dit, et de quand il date.</summary>
public sealed record ReglagesCodLus(
    string Fichier, DateTime ModifieLe,
    string? Reflex, string? VSync, bool? CapFps, int? MaxFpsEnJeu, string? ModeAffichage, bool? GenerationDImages,
    string? TechniqueDUpscaling = null, string? ModeDlss = null);

/// <summary>
/// 24/09/2026 — les reglages du jeu, lus, jamais ecrits.
///
/// Les leviers de latence de rendu de DrDXT ecrivent dans le pilote NVIDIA : sur une machine ou le
/// pilote est ferme (instabilite, decision de l'utilisateur), ils ne peuvent rien. Mais les gains de
/// latence les plus directs vivent dans le JEU lui-meme — Call of Duty expose NVIDIA Reflex et un
/// plafond d'images dans son fichier `players\s.1.0.cod*.txt0`, en texte : `Cle@... = valeur // aide`.
/// DrDXT le lit et dit ce qui coute des millisecondes. Il n'ecrit rien : le jeu reecrit ce fichier,
/// et un outil tiers qui modifie la configuration d'un jeu protege par un anti-triche n'a rien a y faire.
///
/// Sources des constats : NVIDIA Reflex (developer.nvidia.com/performance-rendering-tools/reflex :
/// supprime la file de rendu quand le GPU limite) ; NVIDIA DLSS Frame Generation (la generation
/// d'images ajoute de la latence, d'ou l'activation obligatoire de Reflex avec elle).
/// </summary>
public static class ReglagesCod
{
    /// <summary>Au-dela, les valeurs sont peut-etre celles d'une ancienne version du jeu.</summary>
    public static readonly TimeSpan AgeMaximal = TimeSpan.FromDays(30);

    public static ReglagesCodLus Lire(string fichier, string texte, DateTime modifieLe)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brute in texte.Split('\n'))
        {
            var ligne = brute.Trim();
            if (ligne.Length == 0 || ligne.StartsWith("//", StringComparison.Ordinal)) continue;
            var egal = ligne.IndexOf('=');
            if (egal <= 0) continue;
            var cle = ligne[..egal].Trim();
            var arobase = cle.IndexOf('@');
            if (arobase > 0) cle = cle[..arobase];
            var valeur = ligne[(egal + 1)..];
            var commentaire = valeur.IndexOf("//", StringComparison.Ordinal);
            if (commentaire >= 0) valeur = valeur[..commentaire];
            v[cle] = valeur.Trim();
        }

        string? S(string k) => v.TryGetValue(k, out var x) ? x : null;
        bool? B(string k) => S(k) is { } x ? x.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
        int? I(string k) => int.TryParse(S(k), out var x) ? x : null;

        return new ReglagesCodLus(fichier, modifieLe, S("NvidiaReflex"), S("VSync"), B("CapFps"), I("MaxFpsInGame"),
            S("PreferredDisplayMode") ?? S("DisplayMode"), B("DLSSFrameGeneration"),
            S("DLSSModeMP"), S("DLSSPerfModeMP"));
    }

    /// <summary>29/09/2026 : la part de la résolution, par côté, que chaque mode DLSS calcule avant d'agrandir.
    /// Source : NVIDIA, guide de programmation DLSS (DLSS Programming Guide), table des facteurs d'échelle.
    /// Clés = valeurs écrites par le jeu (« Balanced » lu sur la machine de référence le 25/09).</summary>
    private static readonly Dictionary<string, double> EchelleDlss = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DLAA"] = 1.0, ["Quality"] = 0.667, ["Balanced"] = 0.58, ["Performance"] = 0.5, ["Ultra Performance"] = 0.333,
    };

    public const string Domaine = "LATENCE";

    /// <summary>Les constats. Rien qui ne soit lu ; rien d'affirme sur un fichier trop vieux sans le dire.</summary>
    public static IReadOnlyList<RepairFinding> Constats(ReglagesCodLus r, DateTime maintenant)
    {
        var c = new List<RepairFinding>();
        var vieux = maintenant - r.ModifieLe > AgeMaximal
            ? F($" (fichier du {r.ModifieLe:dd/MM/yyyy} : si le jeu a changé de version depuis, ces valeurs sont peut-être dépassées)")
            : "";

        if (r.Reflex is { } reflex && reflex.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            c.Add(new RepairFinding(Domaine, "NVIDIA Reflex dans le jeu", "désactivé", "Enabled", RepairStatus.NonDefault,
                "Reflex est désactivé dans les réglages du jeu. Quand la carte graphique limite, il supprime la file "
                + "d'attente des images : c'est le gain de latence le plus direct, et il se règle dans le jeu, pas dans "
                + "DrDXT." + vieux, RepairAction.Recommandation));

        if (r.GenerationDImages == true)
            c.Add(new RepairFinding(Domaine, "génération d'images DLSS", "activée", "désactivée", RepairStatus.NonDefault,
                "La génération d'images affiche plus d'images mais ajoute de la latence : en compétitif, chaque image "
                + "générée arrive après celle que le jeu a calculée." + vieux, RepairAction.Recommandation));

        // 29/09/2026 : en partie, la carte de la machine de référence tourne à 97 % ; ses FPS se jouent dans la carte,
        // et le levier logiciel qui reste est le mode DLSS du jeu. DrDXT dit ce que le mode Performance retire, pas
        // un gain qu'il n'a pas mesuré, et n'écrit rien dans le fichier du jeu.
        var dlssConstat = r.TechniqueDUpscaling is { } tech && tech.Equals("DLSS", StringComparison.OrdinalIgnoreCase)
            && r.ModeDlss is { } mode && EchelleDlss.TryGetValue(mode, out var echelle) && echelle > EchelleDlss["Performance"]
            ? new RepairFinding(Domaine, "mode DLSS de Call of Duty", mode, null, RepairStatus.Observed,
                F($"DLSS en « {mode} » : la carte calcule l'image à {echelle * 100:0} % de ta résolution sur chaque côté, puis l'agrandit. ")
                + F($"En « Performance », elle la calcule à 50 % : environ {(1 - 0.25 / (echelle * echelle)) * 100:0} % de pixels en moins, donc plus d'images quand la carte est pleine, contre une image un peu plus douce. ")
                + "Le gain sur ta machine n'est pas mesuré. Ça se règle dans le jeu ; DrDXT n'écrit pas dans ses fichiers. "
                + "Facteurs d'échelle : guide de programmation DLSS de NVIDIA." + vieux)
            : null;

        if (c.Count == 0 && r.Reflex is not null)
            // 25/09/2026 (photo) : « Reflex Enabled + boost » se lisait comme un mot de DrDXT à moitié
            // traduit. C'est la valeur exacte du fichier du jeu : citée comme telle, avec sa source.
            c.Add(new RepairFinding(Domaine, "réglages de latence de Call of Duty", $"Reflex « {r.Reflex} » (fichier de réglages de Call of Duty)", null, RepairStatus.Observed,
                $"Reflex : {r.Reflex} ; plafond d'images en jeu : {(r.CapFps == true ? r.MaxFpsEnJeu?.ToString() ?? "?" : "aucun")} ; "
                + $"V-Sync : {r.VSync ?? "?"} ; affichage : {r.ModeAffichage ?? "?"}." + vieux));
        if (dlssConstat is not null) c.Add(dlssConstat);
        return c;
    }

    /// <summary>Le fichier le plus recent parmi les emplacements connus, ou null.
    /// Mesure du 24/09/2026 sur la machine de reference : la version courante du jeu (cod25) ecrit dans
    /// `%LOCALAPPDATA%\Activision\Call of Duty\players`, et ALTERNE entre `.txt0` et `.txt1` (le plus
    /// recent etait `.txt1`). Les dossiers « Documents » ne portaient qu'une ancienne version (cod24,
    /// juin) : lire la par defaut aurait rendu des reglages perimes.</summary>
    public static FileInfo? Trouver() => PlusRecent(new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activision", "Call of Duty", "players"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Call of Duty", "players"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Call of Duty", "players"),
    });

    public static FileInfo? PlusRecent(IEnumerable<string> dossiers) => dossiers
        .Where(Directory.Exists)
        .SelectMany(d => new DirectoryInfo(d).GetFiles("s.*.cod*.txt*"))
        .Where(f => f.Name.EndsWith(".txt0", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".txt1", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(f => f.LastWriteTimeUtc)
        .FirstOrDefault();
}
