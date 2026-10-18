using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// La version minimale du pilote graphique qu'un jeu exige lui-même, lue dans le fichier qu'il livre avec son installation
/// (driverVersions.json), contre la version en service de chaque carte. Format relevé le 16/10/2026 sur une installation
/// réelle de Call of Duty (_retail_\cod25\driverVersions.json) : un objet « driver_versions », une famille de cartes par clé
/// (nvidia, quadro, nvidia_maxwell_pascal, amd, amdpolarisvega, intel), une version en groupes d'entiers par famille
/// ([591, 59] pour NVIDIA 591.59, [25, 12, 1] pour Adrenalin 25.12.1, [32, 0, 101, 8331] pour Intel). Le format n'est pas
/// documenté par l'éditeur : un fichier qu'on ne sait pas lire rend « non lu ». Un jeu qui ne livre pas ce fichier n'impose
/// pas de plancher : rien n'est dit. La version AMD est celle de la carte présente (clé dont la description est le nom de la
/// carte), jamais la première clé de classe venue, qui peut être celle d'une carte retirée.
/// </summary>
public static class PiloteExigeParLeJeu
{
    public const string Fichier = "driverVersions.json";

    /// <summary>Les fichiers de versions livrés par les jeux, aux seuls emplacements relevés : à la racine du dossier du jeu,
    /// ou sous « _retail_ » et ses sous-dossiers directs (Call of Duty : _retail_\cod25). Steam donne une bibliothèque, ses
    /// jeux sont sous steamapps\common ; les autres lanceurs donnent le dossier du jeu, ou un dossier qui les range. Aucune
    /// recherche récursive : le parcours aveugle de la bibliothèque coûtait ~155 ms par scan (mesuré le 16/10/2026 sur la
    /// machine de référence) et ne descendait pas assez pour une installation Steam.</summary>
    public static IReadOnlyList<string> Trouver(IEnumerable<DossierDeJeux> dossiers)
    {
        var trouves = new List<string>();
        foreach (var d in dossiers)
            try
            {
                var jeux = d.Lanceur == "Steam"
                    ? SousDossiers(Path.Combine(d.Chemin, "steamapps", "common"))
                    : [d.Chemin, .. SousDossiers(d.Chemin)];
                foreach (var jeu in jeux)
                    trouves.AddRange(Emplacements(jeu).Where(File.Exists));
            }
            catch (Exception) { /* un dossier illisible n'empêche pas les autres */ }
        return trouves.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SousDossiers(string dossier)
        => Directory.Exists(dossier) ? Directory.EnumerateDirectories(dossier) : [];

    private static IEnumerable<string> Emplacements(string jeu)
    {
        yield return Path.Combine(jeu, Fichier);
        var retail = Path.Combine(jeu, "_retail_");
        yield return Path.Combine(retail, Fichier);
        foreach (var titre in SousDossiers(retail)) yield return Path.Combine(titre, Fichier);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Quand, string Texte)> Lus =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le texte du fichier, relu seulement si sa date de modification a changé depuis la dernière lecture ;
    /// <c>null</c> = illisible.</summary>
    public static string? Contenu(string chemin)
    {
        try
        {
            var quand = File.GetLastWriteTimeUtc(chemin);
            if (Lus.TryGetValue(chemin, out var lu) && lu.Quand == quand) return lu.Texte;
            var texte = File.ReadAllText(chemin);
            Lus[chemin] = (quand, texte);
            return texte;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : famille ⇒ version exigée ; <c>null</c> si le fichier n'a pas la forme relevée.</summary>
    public static IReadOnlyDictionary<string, int[]>? Lire(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("driver_versions", out var v) || v.ValueKind != JsonValueKind.Object) return null;
            return v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array)
                .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : la famille du fichier qui décrit cette carte ; <c>null</c> si aucune (carte virtuelle, autre marque).</summary>
    public static string? Famille(string modele)
    {
        bool M(string motif) => System.Text.RegularExpressions.Regex.IsMatch(modele, motif, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (M(@"\bQuadro\b|\bRTX\s+A\d{3,4}\b|\bRTX\s+\d{4}\s+Ada\b")) return "quadro";
        if (M(@"\bGTX\s+(9\d\d|10\d0)\b|\bTITAN\s+X")) return "nvidia_maxwell_pascal";
        if (M(@"NVIDIA|GeForce")) return "nvidia";
        if (M(@"\bRX\s+[45]\d0\b|\bVega\b")) return "amdpolarisvega";
        if (M(@"Radeon")) return "amd";
        if (M(@"Intel.*(Arc|Iris|UHD|Graphics)")) return "intel";
        return null;
    }

    /// <summary>Pur : la version en service au format de la famille (NVIDIA 591.59, AMD 25.12.1, Intel 32.0.101.8331).</summary>
    public static int[]? VersionInstallee(string famille, string? versionWindows, string? versionRadeon)
        => famille.StartsWith("amd", StringComparison.OrdinalIgnoreCase)
            ? versionRadeon is null ? null : VersionDuPilote.Groupes(versionRadeon)
            : famille.StartsWith("nvidia", StringComparison.OrdinalIgnoreCase) || famille == "quadro"
                ? versionWindows is null || VersionDuPilote.VersionNvidia(versionWindows) is not { } nv ? null : VersionDuPilote.Groupes(nv)
                : versionWindows is null ? null : VersionDuPilote.Groupes(versionWindows);

    public sealed record Carte(string Modele, string? VersionWindows, string? VersionRadeon);

    /// <summary>Pur. Pour chaque fichier et chaque carte décrite : sous le plancher, au-dessus, ou non lu.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<(string Chemin, string? Contenu)> fichiers, IReadOnlyList<Carte> cartes)
    {
        foreach (var (chemin, contenu) in fichiers)
        {
            var jeu = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(chemin))) is { Length: > 0 } j && j.StartsWith('_')
                ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(chemin)))) ?? chemin
                : Path.GetFileName(Path.GetDirectoryName(chemin)) ?? chemin;
            var nom = Docteur.Nom("PX.Nom", jeu);
            var exige = contenu is null ? null : Lire(contenu);
            if (exige is null)
            {
                yield return new RepairFinding("GPU DRIVER", nom, null, null, RepairStatus.Unknown, Docteur.T("PX.Illisible", chemin));
                continue;
            }
            foreach (var c in cartes)
            {
                if (Famille(c.Modele) is not { } famille || !exige.TryGetValue(famille, out var plancher)) continue;
                var p = string.Join('.', plancher);
                if (VersionInstallee(famille, c.VersionWindows, c.VersionRadeon) is not { } v)
                {
                    yield return new RepairFinding("GPU DRIVER", nom, null, p, RepairStatus.Unknown, Docteur.T("PX.VersionNonLue", c.Modele, p));
                    continue;
                }
                var installee = string.Join('.', v);
                yield return VersionDuPilote.Comparer(v, plancher) < 0
                    ? new RepairFinding("GPU DRIVER", nom, installee, p, RepairStatus.Suspicious,
                        Docteur.T("PX.Sous", jeu, c.Modele, installee, p), RepairAction.Recommandation, Remede: Docteur.T("PX.Remede"))
                    : new RepairFinding("GPU DRIVER", nom, installee, p, RepairStatus.ExpectedDefault, Docteur.T("PX.AuDessus", jeu, c.Modele, installee, p));
            }
        }
    }
}
