using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Diffusion;

/// <summary>
/// OBS Studio : ses réglages de sortie, et le bilan de chaque lancement des 14 derniers jours (images sautées à l'encodage,
/// manquées au rendu, jetées par le réseau). Pourquoi le jeu saccade seulement pendant un stream. Lecture seule ; DrDXT
/// n'écrit jamais dans OBS. Les noms des sorties ne sont jamais repris : ils portent parfois l'identifiant d'un stream.
/// </summary>
/// <remarks>
/// Les lignes de bilan sont celles du code d'OBS 32.0.0 : libobs/obs-output.c (« Number of lagged frames due to rendering
/// lag/stalls: %u (%0.1f%%) », « Number of dropped frames due to insufficient bandwidth/connection stalls: %d (%0.1f%%) ») et
/// libobs/media-io/video-io.c (« skipped frames due to encoding lag: %ld/%ld (%0.1f%%) »). Les seuils sont ceux de la fenêtre
/// de statistiques d'OBS (frontend/widgets/OBSBasicStats.cpp) : au-dessus de 1 % en avertissement, de 5 % en erreur. Les
/// réglages relevés sur la machine de référence le 17/10/2026 (OBS 32.2.2) : user.ini [Basic] ProfileDir ; basic.ini [Output]
/// Mode, [SimpleOutput] StreamEncoder et VBitrate, [AdvOut] Encoder, [Video] OutputCX et OutputCY ; streamEncoder.json
/// « bitrate ». Le même jour, dix journaux, dont des lancements à 9 à 17 % d'images sautées à l'encodage.
/// </remarks>
public static partial class ObsStudio
{
    public sealed record Reglages(string? Mode, string? Encodeur, int? DebitKbps, int? Largeur, int? Hauteur);

    /// <summary>Un lancement d'OBS qui a diffusé ou enregistré ; les parts en %, null = aucune image comptée pour ce type.
    /// <see cref="Reconnu"/> faux = une sortie s'est arrêtée mais aucune ligne de bilan n'a été reconnue.</summary>
    public sealed record Session(DateTime Quand, double? Encodage, double? Rendu, double? Reseau, bool Reconnu);

    public const int JoursLus = 14;
    public const double SeuilAvertissement = 1.0;
    public const double SeuilErreur = 5.0;
    private const long OctetsMax = 8L * 1024 * 1024;
    private const int JournauxMax = 30;

    // [0-9] et non \d : \d accepte les chiffres de toutes les écritures, qu'OBS n'écrit jamais.
    [GeneratedRegex(@"skipped frames due to encoding lag: (?<n>[0-9]+)/(?<t>[0-9]+) ")]
    private static partial Regex LigneEncodage();
    [GeneratedRegex(@"': Total frames output: (?<a>[0-9]+)(?: \((?<t>[0-9]+) attempted\))?")]
    private static partial Regex LigneSortie();
    [GeneratedRegex(@"': Total drawn frames: (?<a>[0-9]+)(?: \((?<t>[0-9]+) attempted\))?")]
    private static partial Regex LigneDessin();
    [GeneratedRegex(@"Number of lagged frames due to rendering lag/stalls: (?<n>[0-9]+) ")]
    private static partial Regex LigneRendu();
    [GeneratedRegex(@"Number of dropped frames due to insufficient bandwidth/connection stalls: (?<n>[0-9]+) ")]
    private static partial Regex LigneReseau();
    [GeneratedRegex(@"Output '.*': stopping")]
    private static partial Regex LigneArret();

    /// <summary>
    /// Pur : le bilan d'un journal ; null quand aucune sortie ne s'y est arrêtée (OBS ouvert sans diffuser). Chaque part est
    /// pondérée par les images : la somme des pertes sur la somme des images tentées de toutes les sorties. Une sortie lancée
    /// puis coupée aussitôt (29 images manquées sur 42, relevé le 17/10/2026) ne fait pas passer un lancement pour perdu à 69 %.
    /// </summary>
    public static Session? Bilan(string journal, DateTime quand)
    {
        if (!LigneArret().IsMatch(journal)) return null;
        long encN = 0, encT = 0, renduN = 0, renduT = 0, reseauN = 0, reseauT = 0;
        var reconnu = false;
        // OBS écrit ces nombres en %u / %ld (32 bits sous Windows) : au-delà, la ligne est abîmée et sautée. Les sommes en
        // long ne débordent pas.
        static bool L(Group g, out long v)
        {
            var ok = int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i);
            v = i;
            return ok;
        }
        static Group TA(Match m) => m.Groups["t"].Success ? m.Groups["t"] : m.Groups["a"];
        foreach (var ligne in journal.Split('\n'))
        {
            if (LigneEncodage().Match(ligne) is { Success: true } e)
            {
                if (L(e.Groups["n"], out var n) && L(e.Groups["t"], out var t)) { encN += n; encT += t; reconnu = true; }
            }
            else if (LigneSortie().Match(ligne) is { Success: true } o) { if (L(TA(o), out var t)) { reseauT += t; reconnu = true; } }
            else if (LigneDessin().Match(ligne) is { Success: true } d) { if (L(TA(d), out var t)) { renduT += t; reconnu = true; } }
            else if (LigneRendu().Match(ligne) is { Success: true } r) { if (L(r.Groups["n"], out var n)) renduN += n; }
            else if (LigneReseau().Match(ligne) is { Success: true } x) { if (L(x.Groups["n"], out var n)) reseauN += n; }
        }
        // Plus d'images perdues que d'images tentées, c'est un journal abîmé, pas une part : jamais « 99900 % ».
        static double? Part(long n, long t) => t > 0 && n <= t ? 100.0 * n / t : null;
        return new Session(quand, Part(encN, encT), Part(renduN, renduT), Part(reseauN, reseauT), reconnu);
    }

    /// <summary>Pur : une valeur d'un fichier ini d'OBS, section et clé exactes.</summary>
    public static string? Ini(string texte, string section, string cle)
    {
        var dans = false;
        foreach (var brut in texte.Split('\n'))
        {
            var l = brut.Trim();
            if (l.StartsWith('[')) { dans = l == "[" + section + "]"; continue; }
            if (dans && l.StartsWith(cle + "=", StringComparison.Ordinal)) return l[(cle.Length + 1)..];
        }
        return null;
    }

    public static string Dossier => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    public static Reglages? LireReglages(string dossier)
    {
        var user = Lire(Path.Combine(dossier, "user.ini")) ?? Lire(Path.Combine(dossier, "global.ini"));
        // Un nom de profil sous le dossier d'OBS : un chemin absolu ferait lire ailleurs, et un chemin réseau ouvrirait une
        // connexion SMB, donc une authentification du compte, vers l'hôte nommé.
        if (user is null || Ini(user, "Basic", "ProfileDir") is not { Length: > 0 } profil || profil.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(profil)) return null;
        var dossierProfil = Path.Combine(dossier, "basic", "profiles", profil);
        if (Lire(Path.Combine(dossierProfil, "basic.ini")) is not { } basic) return null;
        var mode = Ini(basic, "Output", "Mode");
        var avance = string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase);
        var encodeur = avance ? Ini(basic, "AdvOut", "Encoder") : Ini(basic, "SimpleOutput", "StreamEncoder");
        int? debit = null;
        if (avance)
        {
            try
            {
                if (Lire(Path.Combine(dossierProfil, "streamEncoder.json")) is { } json)
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("bitrate", out var b)
                        && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var kbps))
                        debit = kbps;
                }
            }
            catch (JsonException) { }
        }
        else if (int.TryParse(Ini(basic, "SimpleOutput", "VBitrate"), CultureInfo.InvariantCulture, out var v)) debit = v;
        int? Entier(string cle) => int.TryParse(Ini(basic, "Video", cle), CultureInfo.InvariantCulture, out var n) ? n : null;
        return new Reglages(mode, encodeur, debit, Entier("OutputCX"), Entier("OutputCY"));
    }

    public static IReadOnlyList<Session>? LireSessions(string dossier, DateTime maintenant)
    {
        var logs = Path.Combine(dossier, "logs");
        if (!Directory.Exists(logs)) return null;
        var sessions = new List<Session>();
        foreach (var f in new DirectoryInfo(logs).EnumerateFiles("*.txt")
                     .Where(f => f.LastWriteTime >= maintenant.AddDays(-JoursLus))
                     .OrderByDescending(f => f.LastWriteTime).Take(JournauxMax))
            if (Lire(f.FullName) is { } t && Bilan(t, f.LastWriteTime) is { } s) sessions.Add(s);
        return sessions;
    }

    // Fichier non fiable : taille plafonnée, lecture partagée (OBS peut l'avoir ouvert).
    private static string? Lire(string chemin)
    {
        try
        {
            using var fs = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > OctetsMax) return null;
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Le pire lancement des 14 jours, cause par cause, avec les seuils d'OBS.</summary>
    public static IEnumerable<RepairFinding> Juger(bool installe, Reglages? reglages, IReadOnlyList<Session>? sessions)
    {
        if (!installe)
        {
            yield return new RepairFinding("STREAM", "OBS Studio", Docteur.T("OBS.ValeurAbsent"), null, RepairStatus.ExpectedDefault, Docteur.T("OBS.Absent"));
            yield break;
        }
        yield return reglages is null
            ? new RepairFinding("STREAM", "configuration d'OBS", null, null, RepairStatus.Unknown, Docteur.T("OBS.ReglagesNonLus"))
            : new RepairFinding("STREAM", "configuration d'OBS", Resume(reglages), null, RepairStatus.Observed,
                Docteur.T(EstLogiciel(reglages.Encodeur) ? "OBS.Logiciel" : "OBS.Reglages"));
        if (sessions is null)
        {
            yield return new RepairFinding("STREAM", "pertes en stream", null, null, RepairStatus.Unknown, Docteur.T("OBS.JournauxNonLus"));
            yield break;
        }
        // Un bilan reconnu sans aucune image comptée (une sortie coupée à 0 image) ne mesure rien : il ne compte pas.
        var lues = sessions.Where(s => s.Reconnu && (s.Encodage ?? s.Rendu ?? s.Reseau) is not null).ToList();
        if (lues.Count == 0)
        {
            // Des sorties arrêtées sans bilan reconnu : le format a changé, on ne fabrique pas un « 0 perte ».
            yield return sessions.Count == 0
                ? new RepairFinding("STREAM", "pertes en stream", Docteur.T("OBS.ValeurAucune"), null, RepairStatus.Observed, Docteur.T("OBS.AucuneSession", JoursLus))
                : new RepairFinding("STREAM", "pertes en stream", null, null, RepairStatus.Unknown, Docteur.T("OBS.BilanNonReconnu", sessions.Count));
            yield break;
        }
        // Une cause qu'aucun lancement n'a comptée se dit non comptée, jamais « 0 % ».
        static double? Pire(IEnumerable<double?> parts) => parts.OfType<double>().DefaultIfEmpty(double.NaN).Max() is var m && double.IsNaN(m) ? null : m;
        var encodage = Pire(lues.Select(s => s.Encodage));
        var rendu = Pire(lues.Select(s => s.Rendu));
        var reseau = Pire(lues.Select(s => s.Reseau));
        var (cle, pire) = new[] { ("OBS.Encodage", encodage ?? 0), ("OBS.Rendu", rendu ?? 0), ("OBS.Reseau", reseau ?? 0) }.MaxBy(x => x.Item2);
        static string Part(double? p) => p is { } v ? $"{Pct(v)} %" : Docteur.T("OBS.NonCompte");
        var valeur = Docteur.T("OBS.ValeurPertes", Part(encodage), Part(rendu), Part(reseau));
        yield return pire > SeuilErreur
            ? new RepairFinding("STREAM", "pertes en stream", valeur, "≤ 1 %", RepairStatus.Suspicious,
                Docteur.T(cle, Pct(pire), lues.Count, JoursLus), RepairAction.Recommandation)
            : new RepairFinding("STREAM", "pertes en stream", valeur, "≤ 1 %", pire > SeuilAvertissement ? RepairStatus.Observed : RepairStatus.ExpectedDefault,
                Docteur.T(pire > SeuilAvertissement ? "OBS.Quelques" : "OBS.Propres", lues.Count, JoursLus));
    }

    /// <summary>x264 est l'encodeur logiciel d'OBS (le processeur encode) ; les autres sont ceux d'une carte.</summary>
    public static bool EstLogiciel(string? encodeur) => encodeur?.Contains("x264", StringComparison.OrdinalIgnoreCase) == true;

    private static string Resume(Reglages r)
    {
        var morceaux = new List<string>();
        if (r.Encodeur is { } e) morceaux.Add(e);
        if (r.DebitKbps is { } d) morceaux.Add(d.ToString(Docteur.Langue) + " kbit/s");
        if (r.Largeur is { } l && r.Hauteur is { } h) morceaux.Add($"{l}×{h}");
        return morceaux.Count == 0 ? "?" : string.Join(", ", morceaux);
    }

    private static string Pct(double p) => p.ToString("0.#", Docteur.Langue);
}
