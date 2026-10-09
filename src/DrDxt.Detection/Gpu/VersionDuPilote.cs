using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>Une version de pilote publiée par le fabricant, avec sa date de sortie.</summary>
public sealed record VersionPubliee(string Version, DateOnly? Sortie);

/// <summary>
/// 03/10/2026 (tour G, retour d'usage : « DrDXT ne m'a jamais dit que mes pilotes GPU n'étaient pas bons »). Hors ligne,
/// DrDXT ne savait pas qu'une version plus récente existait : la branche 616 passait le plancher « 500 » et le pilote,
/// trois versions en retard, était déclaré à jour. DrDXT demande maintenant au fabricant de la carte ses dernières
/// versions publiées, et compte le retard du pilote installé.
/// </summary>
public static class VersionDuPilote
{
    public const string Domaine = "GPU DRIVER";
    public const string Nom = "version du pilote graphique";

    /// <summary>« 617.14 » &gt; « 616.92 » : comparaison numérique groupe par groupe ; <c>null</c> si illisible.</summary>
    internal static int[]? Groupes(string version)
    {
        var parts = version.Trim().Split('.');
        var g = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out g[i])) return null;
        return g;
    }

    internal static int Comparer(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>La version NVIDIA depuis la forme Windows (« 32.0.16.1656 » ⇒ « 616.56 » : dernier chiffre du 3e groupe et
    /// 4e groupe) ; une version déjà au format NVIDIA (« 616.56 », lue par NVML) passe telle quelle.</summary>
    public static string? VersionNvidia(string version)
    {
        var g = Groupes(version);
        if (g is null) return null;
        if (g.Length != 4) return version.Trim();
        var n = g[2] % 10 * 10000 + g[3];
        return $"{n / 100}.{n % 100:00}";
    }

    /// <summary>La vérification complète pour les cartes de la machine : NVIDIA par sa source ; AMD et Intel nommés avec
    /// l'endroit où vérifier, tant que leur source n'est pas lue.</summary>
    public static async Task<RepairFinding?> VerifierAsync(IReadOnlyList<GpuInfo> cartes, SourceNvidia nvidia, CancellationToken ct = default)
        => await VerifierAsync(cartes, nvidia, null, null, ct).ConfigureAwait(false);

    /// <param name="amd">03/10/2026 (G4) : la page de téléchargement d'AMD.</param>
    /// <param name="versionRadeon">La version AMD installée de la carte nommée, au format d'AMD (« 26.8.1 »), lue dans le registre.</param>
    public static async Task<RepairFinding?> VerifierAsync(IReadOnlyList<GpuInfo> cartes, SourceNvidia nvidia, SourceAmd? amd,
        Func<string, string?>? versionRadeon, CancellationToken ct = default)
    {
        if (cartes.FirstOrDefault(c => c.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) is { } nv)
            return !SuitLesPilotesGameReady(nv.Model)
                ? SansSource("NVIDIA", VersionNvidia(nv.DriverVersion) ?? nv.DriverVersion,
                    "nvidia.com/fr-fr/drivers en choisissant ta carte (DrDXT ne compare que les GeForce RTX 20 à 50, GTX 16 et "
                    + "MX 400 et 500 : les GTX 900 et 10 ne reçoivent plus de nouvelle version depuis la branche 580, et les autres "
                    + "cartes suivent leur propre suite de pilotes)")
                : VersionNvidia(nv.DriverVersion) is not { } version ? null
                // Revue R2 : site muet (hors ligne, page changée) = l'endroit où vérifier, pas le silence.
                : await nvidia.DernieresAsync(ct).ConfigureAwait(false) is { } publiees ? Juger("NVIDIA", version, publiees)
                : SansReponse("NVIDIA", version, "nvidia.com/fr-fr/drivers");
        if (cartes.FirstOrDefault(c => c.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)) is { } radeon)
        {
            if (!SuitAdrenalin(radeon.Model))
                // Revue R2 : « les cartes plus anciennes » était faux pour une puce intégrée récente (« AMD Radeon(TM) Graphics »).
                return SansSource("AMD", radeon.DriverVersion, "amd.com/fr/support en choisissant ta carte (DrDXT ne compare que les "
                    + "Radeon RX 5000 à 9000)");
            if (amd is null || versionRadeon?.Invoke(radeon.Model) is not { } installee)
                return SansSource("AMD", radeon.DriverVersion, "amd.com/fr/support");
            return await amd.DerniereAsync(ct).ConfigureAwait(false) is { } derniere
                ? Juger("AMD", installee, [derniere], listeComplete: false)
                : SansReponse("AMD", installee, "amd.com/fr/support");
        }
        if (cartes.FirstOrDefault(c => c.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) is { } intel)
            // G4, relevé le 03/10/2026 : la page de téléchargement d'Intel répond 403 à tout programme, même avec les en-têtes
            // d'un navigateur. Pas de source lisible : l'endroit où vérifier est dit.
            return SansSource("Intel", intel.DriverVersion, "intel.fr, page « Pilotes graphiques » (le site d'Intel refuse la lecture par un programme)");
        return null;
    }

    /// <summary>
    /// Revue C1 (03/10, « tous les autres PC ? ») : la source lue est celle des GeForce récentes. NVIDIA a arrêté les nouvelles
    /// versions pour Maxwell, Pascal et Volta à la branche 580 (annonce de juillet 2025, reprise par Tom's Hardware et
    /// TechPowerUp) : une GTX 1060 se verrait dire « en retard » sur des versions qui ne la prennent pas en charge. Les
    /// cartes professionnelles (Quadro, RTX A) suivent une autre suite. Seules les GeForce qui reçoivent la même version
    /// Game Ready que la RTX 40 de référence sont donc comparées : RTX 20 à 50, GTX 16, MX 400 et 500.
    /// </summary>
    internal static bool SuitLesPilotesGameReady(string modele) =>
        System.Text.RegularExpressions.Regex.IsMatch(modele, @"GeForce\s+(RTX\s+[2-5]0\d0|GTX\s+16\d0|MX\s*[45]\d0)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>G4 : les Radeon qui reçoivent la version d'Adrenalin publiée en tête de la page d'AMD (RDNA, RX 5000 à 9000).</summary>
    internal static bool SuitAdrenalin(string modele) =>
        System.Text.RegularExpressions.Regex.IsMatch(modele, @"Radeon\s+RX\s+[5-9]\d{3}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>La version AMD installée au format d'AMD, que Windows garde dans la clé de la carte graphique
    /// (valeur « RadeonSoftwareVersion », relevée dans des exports de registre publiés sur le forum Guru3D) ; <c>null</c>
    /// sans carte AMD ou sans cette valeur. Revue R2 : les clés des cartes retirées restent ; seule celle dont la description
    /// (« DriverDesc ») est le nom de la carte présente est lue.</summary>
    public static string? RadeonSoftwareVersion(string modele)
    {
        try
        {
            using var classe = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classe is null) return null;
            foreach (var nom in classe.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                try
                {
                    using var carte = classe.OpenSubKey(nom);
                    if (carte?.GetValue("DriverDesc") is string desc && string.Equals(desc.Trim(), modele.Trim(), StringComparison.OrdinalIgnoreCase)
                        && carte.GetValue("RadeonSoftwareVersion") is string v && Groupes(v) is not null) return v.Trim();
                }
                catch { /* une clé refusée n'empêche pas de lire les autres */ }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Le constat : à jour, ou le nombre de versions publiées plus récentes et la dernière.</summary>
    /// <param name="listeComplete">G4 : faux quand la source ne donne que la dernière version (AMD) ; le retard n'est alors
    /// pas compté, il est dit.</param>
    public static RepairFinding? Juger(string marque, string installee, IReadOnlyList<VersionPubliee> publiees, bool listeComplete = true)
    {
        if (Groupes(installee) is not { } mienne || publiees.Count == 0) return null;
        var plusRecentes = publiees
            .Select(p => (p, g: Groupes(p.Version)))
            .Where(x => x.g is not null && Comparer(x.g!, mienne) > 0)
            .OrderByDescending(x => x.g!, Comparer<int[]>.Create(Comparer))
            .Select(x => x.p).ToList();
        if (plusRecentes.Count == 0)
            return new RepairFinding(Domaine, Nom, $"{marque} {installee}", null, RepairStatus.ExpectedDefault,
                $"ton pilote {marque} {installee} est la dernière version publiée par {marque}.");
        var derniere = plusRecentes[0];
        var date = derniere.Sortie is { } d ? $" du {d:dd/MM/yyyy}" : "";
        var retard = !listeComplete ? "n'est pas le dernier"
            : plusRecentes.Count == 1 ? "a une version de retard" : $"a {plusRecentes.Count} versions de retard";
        // Revue adverse du 09/10/2026 (R3) : être en retard n'est pas un défaut ; observé, avec son geste (Recommandation).
        return new RepairFinding(Domaine, Nom, $"{marque} {installee}", derniere.Version, RepairStatus.Observed,
            $"ton pilote {marque} {installee} {retard} : la dernière publiée est la {derniere.Version}{date}. Mets-le à jour "
            + $"depuis l'application {marque} ou le site de {marque} ; DrDXT ne l'installe pas lui-même.",
            RepairAction.Recommandation);
    }

    /// <summary>Revue R2 : la source existe mais n'a pas répondu cette fois.</summary>
    public static RepairFinding SansReponse(string marque, string installee, string ouRegarder) =>
        new(Domaine, Nom, $"{marque} {installee}", null, RepairStatus.Unknown,
            $"le site de {marque} n'a pas répondu (hors ligne, ou page changée) : DrDXT n'a pas pu comparer ton pilote "
            + $"{installee} à la dernière version. Vérifie sur {ouRegarder}.");

    /// <summary>Pas de source lisible pour cette marque (AMD, Intel) : le dire, et où regarder.</summary>
    public static RepairFinding SansSource(string marque, string installee, string ouRegarder) =>
        new(Domaine, Nom, $"{marque} {installee}", null, RepairStatus.Observed,
            $"DrDXT ne sait pas encore lire la dernière version publiée par {marque} : vérifie sur {ouRegarder}.");
}

/// <summary>
/// 03/10/2026 (G4) : la page de téléchargement d'AMD. Son bouton « Download Windows Drivers » pointe sur l'installeur de la
/// dernière version, dont le nom porte la version et la date (relevé : « adrenalin-edition-26.8.1-minimalsetup-260818 »).
/// Une seule version publiée est donc lisible, la dernière.
/// </summary>
public sealed class SourceAmd
{
    private const string Url = "https://www.amd.com/en/support/download/drivers.html";
    private static readonly System.Text.RegularExpressions.Regex Installeur =
        new(@"adrenalin-edition-(\d+\.\d+\.\d+)-minimalsetup-(\d{6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly Func<string, CancellationToken, Task<string>> _lire;

    public SourceAmd(Func<string, CancellationToken, Task<string>>? lire = null) => _lire = lire ?? LireEnLigne;

    private static async Task<string> LireEnLigne(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DrDXT");
        return await http.GetStringAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>La dernière version publiée, ou <c>null</c> si la page ne répond pas ou change de forme.</summary>
    public async Task<VersionPubliee?> DerniereAsync(CancellationToken ct = default)
    {
        try { return Lire(await _lire(Url, ct).ConfigureAwait(false)); }
        catch { return null; }
    }

    internal static VersionPubliee? Lire(string html)
    {
        var m = Installeur.Match(html);
        if (!m.Success) return null;
        DateOnly? sortie = DateOnly.TryParseExact(m.Groups[2].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
        return new VersionPubliee(m.Groups[1].Value, sortie);
    }
}

/// <summary>
/// La source publique de NVIDIA (celle du formulaire de téléchargement du site), relevée le 03/10/2026 : une requête
/// rend les dernières versions Game Ready WHQL pour Windows 11. La version GeForce est la même pour toutes les cartes
/// de bureau d'une époque ; la série RTX 40 (psid 129, pfid 1004) sert de référence. Seule requête réseau de ce module.
/// </summary>
public sealed class SourceNvidia
{
    private const string Url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php"
        + "?func=DriverManualLookup&psid=129&pfid=1004&osID=135&languageCode=1033&isWHQL=1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=5";

    private readonly Func<string, CancellationToken, Task<string>> _lire;

    public SourceNvidia(Func<string, CancellationToken, Task<string>>? lire = null) => _lire = lire ?? LireEnLigne;

    private static async Task<string> LireEnLigne(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>Les dernières versions publiées, ou <c>null</c> si la source ne répond pas ou change de forme.</summary>
    public async Task<IReadOnlyList<VersionPubliee>?> DernieresAsync(CancellationToken ct = default)
    {
        try { return Lire(await _lire(Url, ct).ConfigureAwait(false)); }
        catch { return null; }
    }

    internal static IReadOnlyList<VersionPubliee>? Lire(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids)) return null;
        var liste = new List<VersionPubliee>();
        foreach (var e in ids.EnumerateArray())
        {
            var info = e.GetProperty("downloadInfo");
            var version = info.GetProperty("Version").GetString();
            if (string.IsNullOrWhiteSpace(version)) continue;
            DateOnly? sortie = info.TryGetProperty("ReleaseDateTime", out var d)
                && DateTime.TryParseExact(d.GetString(), "ddd MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? DateOnly.FromDateTime(dt) : null;
            liste.Add(new VersionPubliee(version, sortie));
        }
        return liste.Count == 0 ? null : liste;
    }
}
