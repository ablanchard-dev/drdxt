using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les rapports que Call of Duty écrit lui-même quand il plante : une archive zip par plantage, dans
/// %LOCALAPPDATA%\Activision\Call of Duty\crash_reports (et son sous-dossier « uploaded »). Le jeu y note l'heure, le
/// code d'exception, une signature de pile, la mémoire et la mémoire vidéo au moment du plantage, et une erreur DirectX
/// quand la carte a été retirée. Lire ce rapport vaut mieux que deviner. Lecture seule : l'archive est ouverte en
/// mémoire, deux fichiers texte y sont lus, rien n'est extrait sur le disque.
/// </summary>
/// <remarks>
/// Le format n'est pas documenté par l'éditeur ; il a été relevé le 16/10/2026 sur la machine de référence (13 archives,
/// décembre 2025 à juillet 2026) : info.json, un objet plat de paires clé-valeur texte, et stacktrace_crash.csv (en-tête
/// « UsedInCrashSignature,FrameIndex,…,ModuleName »). Il peut changer à chaque saison : une clé absente donne « non lu ».
/// </remarks>
public static class RapportsDePlantageCod
{
    /// <param name="Modules">Les modules de la pile qui ne sont ni le jeu ni Windows, par nom de fichier seul.</param>
    public sealed record Rapport(DateTimeOffset Quand, string? Code, string? Signature, long? MemoireLibreMo, long? EngageMo,
        long? LimiteEngagementMo, long? DeficitVideoMo, string? ErreurDirectX, IReadOnlyList<string> Modules);

    /// <summary>Plafonds d'une archive non fiable. **Non chiffrés** : quinze fois les tailles relevées (archive de
    /// 250 Ko, info.json de 11 Ko, pile de 2 Ko).</summary>
    public const long ArchiveMax = 4 << 20, InfoMax = 256 << 10, PileMax = 64 << 10;

    public static readonly TimeSpan Fenetre = TimeSpan.FromDays(90);

    /// <summary>Un même incident vu par une autre source (arrêt brutal, plantage noté par Windows) dans cet écart.
    /// **Non chiffré.**</summary>
    public static readonly TimeSpan MemeIncident = TimeSpan.FromMinutes(15);

    // Microsoft Learn, « DXGI_ERROR » : la carte a été retirée (pilote relancé, mise à jour, panne) ou a cessé de répondre.
    private static readonly Dictionary<string, string> ErreursDxgi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["887A0005"] = "DXGI_ERROR_DEVICE_REMOVED", ["887A0006"] = "DXGI_ERROR_DEVICE_HUNG", ["887A0007"] = "DXGI_ERROR_DEVICE_RESET",
    };

    public static string Dossier() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Activision", "Call of Duty", "crash_reports");

    /// <summary>Les rapports depuis <paramref name="depuis"/> ; null si le dossier existe mais n'a pas pu être lu, vide
    /// s'il n'existe pas (jeu absent ou jamais planté).</summary>
    public static IReadOnlyList<Rapport>? Lire(string dossier, DateTimeOffset depuis)
    {
        try
        {
            if (!Directory.Exists(dossier)) return [];
            return Directory.EnumerateFiles(dossier, "*.zip", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
                .Select(f => new FileInfo(f)).Where(f => f.LastWriteTimeUtc >= depuis.UtcDateTime && f.Length <= ArchiveMax)
                .Select(f => LireArchive(f.FullName)).OfType<Rapport>().Where(r => r.Quand >= depuis)
                .OrderByDescending(r => r.Quand).ToList();
        }
        catch (Exception) { return null; }
    }

    private static Rapport? LireArchive(string chemin)
    {
        try
        {
            using var zip = ZipFile.OpenRead(chemin);
            static string? Texte(ZipArchive z, string nom, long max)
            {
                if (z.GetEntry(nom) is not { } e || e.Length > max) return null;
                using var flux = e.Open();
                return LireBorne(flux, max);
            }
            return Decoder(Texte(zip, "info.json", InfoMax), Texte(zip, "stacktrace_crash.csv", PileMax));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Le texte d'une entrée, au plus <paramref name="max"/> octets comptés à la lecture : la taille que l'entrée
    /// déclare peut mentir. null si elle est plus longue.</summary>
    internal static string? LireBorne(Stream flux, long max)
    {
        var tampon = new byte[max + 1];
        var lus = 0;
        for (int n; lus < tampon.Length && (n = flux.Read(tampon, lus, tampon.Length - lus)) > 0;) lus += n;
        return lus > max ? null : System.Text.Encoding.UTF8.GetString(tampon, 0, lus);
    }

    /// <summary>Pur : un rapport depuis le texte d'info.json et de la pile ; null sans heure de plantage lisible.</summary>
    public static Rapport? Decoder(string? info, string? pile)
    {
        if (info is null) return null;
        Dictionary<string, string> c;
        try
        {
            using var doc = JsonDocument.Parse(info);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            c = doc.RootElement.EnumerateObject().GroupBy(p => p.Name)
                .ToDictionary(g => g.Key, g => g.First().Value.ValueKind == JsonValueKind.String ? g.First().Value.GetString() ?? "" : g.First().Value.ToString());
        }
        catch (JsonException) { return null; }

        string? S(string k) => c.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        long? N(string k) => long.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        // « 2026-07-07 23:45:17(123ms) UTC »
        if (S("crash_time") is not { } t || t.Length < 19
            || !DateTime.TryParseExact(t[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var quand))
            return null;
        var dx = S("gfx_error_removed_code") is { } code ? ErreursDxgi.GetValueOrDefault(code.Replace("0x", "", StringComparison.OrdinalIgnoreCase), code) : null;
        return new Rapport(new DateTimeOffset(quand, TimeSpan.Zero), S("exception_code"), S("crash_signature"),
            N("extch_perf_phys_avail_mb"), N("extch_perf_commit_total_mb"), N("extch_perf_commit_limit_mb"),
            N("vidmem_deficit_mb") is { } v ? v + (N("vidmem_sys_deficit_mb") ?? 0) : null,
            dx, ModulesTiers(pile));
    }

    /// <summary>Pur : les modules de la pile hors du dossier du jeu (celui du premier module) et hors de Windows, nom seul.</summary>
    internal static IReadOnlyList<string> ModulesTiers(string? pile)
    {
        if (pile is null) return [];
        var chemins = pile.Split('\n').Skip(1).Select(l => l.TrimEnd('\r').Split(','))
            .Where(c => c.Length >= 6 && !c[0].StartsWith('#')).Select(c => c[5].Trim()).Where(p => p.Length > 0).ToList();
        var jeu = chemins.Select(Path.GetDirectoryName).FirstOrDefault();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return chemins.Where(p => (jeu is null || !p.StartsWith(jeu, StringComparison.OrdinalIgnoreCase))
                                  && !p.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                                  && !p.StartsWith(@"C:\WINDOWS\", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Pur : les faits, sans accuser. <paramref name="autres"/> : les arrêts brutaux et les plantages notés par
    /// Windows ; un rapport à moins d'un quart d'heure de l'un d'eux est le même incident, dit une fois.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Rapport>? rapports, IReadOnlyList<DateTimeOffset> autres)
    {
        if (rapports is null)
        {
            yield return new RepairFinding("STABILITE", "rapports de plantage de Call of Duty", null, null, RepairStatus.Unknown, Docteur.T("RC.NonLu"));
            yield break;
        }
        if (rapports.Count == 0) yield break;
        var dernier = rapports[0];
        string Mo(long? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : Docteur.T("RC.NonLuCourt");
        var repetee = rapports.Where(r => r.Signature is not null).GroupBy(r => r.Signature).OrderByDescending(g => g.Count()).FirstOrDefault();
        var directx = rapports.Where(r => r.ErreurDirectX is not null).ToList();
        var modules = rapports.SelectMany(r => r.Modules).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var memes = rapports.Count(r => autres.Any(a => (a - r.Quand).Duration() <= MemeIncident));
        var texte = Docteur.T("RC.Compte", rapports.Count, (int)Fenetre.TotalDays,
                dernier.Quand.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue), dernier.Code ?? "?")
            + Docteur.T("RC.Memoire", Mo(dernier.MemoireLibreMo), Mo(dernier.EngageMo), Mo(dernier.LimiteEngagementMo), Mo(dernier.DeficitVideoMo))
            + (repetee is { } g && g.Count() >= 2 ? Docteur.T("RC.Signature", g.Count(), g.Key) : "")
            + (directx.Count > 0 ? Docteur.T("RC.DirectX", directx.Count, string.Join(", ", directx.Select(r => r.ErreurDirectX).Distinct())) : "")
            + (modules.Count > 0 ? Docteur.T("RC.Modules", string.Join(", ", modules)) : "")
            + (memes > 0 ? Docteur.T("RC.MemeIncident", memes) : "")
            + Docteur.T("RC.Faits");
        yield return new RepairFinding("STABILITE", "rapports de plantage de Call of Duty", $"{rapports.Count}", null, RepairStatus.Observed, texte);
    }

    /// <summary>Le vrai, en lecture seule : le dossier du jeu, les arrêts brutaux et les plantages notés par Windows.</summary>
    public static IEnumerable<RepairFinding> DuPc()
    {
        var depuis = DateTimeOffset.UtcNow - Fenetre;
        var rapports = Lire(Dossier(), depuis);
        var arrets = new Hardware.WindowsUnexpectedShutdownLog().Detail(depuis);
        return Juger(rapports, [.. arrets.IsOk ? arrets.Value!.Select(a => a.Quand) : [],
            .. (PlantagesDeJeu.Lire(depuis) ?? []).Select(e => e.Quand)]);
    }
}
