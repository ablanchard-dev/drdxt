using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>Une version de pilote publiée par le fabricant, avec sa date de sortie.</summary>
public sealed record VersionPubliee(string Version, DateOnly? Sortie);

/// <summary>
/// Hors ligne,
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

    /// <param name="amd">La page de téléchargement d'AMD.</param>
    /// <param name="versionRadeon">La version AMD installée de la carte nommée, au format d'AMD (« 26.8.1 »), lue dans le registre.</param>
    public static async Task<RepairFinding?> VerifierAsync(IReadOnlyList<GpuInfo> cartes, SourceNvidia nvidia, SourceAmd? amd,
        Func<string, string?>? versionRadeon, CancellationToken ct = default)
    {
        if (cartes.FirstOrDefault(c => c.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) is { } nv)
            return !SuitLesPilotesGameReady(nv.Model)
                ? SansSource("NVIDIA", VersionNvidia(nv.DriverVersion) ?? nv.DriverVersion,
                    Docteur.T("VP.OuNvidiaAncienne"))
                : VersionNvidia(nv.DriverVersion) is not { } version ? null
                // Site muet (hors ligne, page changée) = l'endroit où vérifier, pas le silence.
                : await nvidia.DernieresAsync(ct).ConfigureAwait(false) is { } publiees ? Juger("NVIDIA", version, publiees)
                : SansReponse("NVIDIA", version, Docteur.T("VP.OuNvidia"));
        // La carte dédiée d'abord, puis la plus grosse mémoire : une APU liste souvent sa puce intégrée en premier.
        if (cartes.Where(c => CartesGraphiques.EstAmd(c.Vendor))
                .OrderByDescending(CartesGraphiques.EstDediee).ThenByDescending(c => c.VramBytes)
                .FirstOrDefault() is { } radeon)
        {
            if (!SuitAdrenalin(radeon.Model))
                // « les cartes plus anciennes » était faux pour une puce intégrée récente (« AMD Radeon(TM) Graphics »).
                return SansSource("AMD", radeon.DriverVersion, Docteur.T("VP.OuAmdAncienne"));
            if (amd is null || versionRadeon?.Invoke(radeon.Model) is not { } installee)
                return SansSource("AMD", radeon.DriverVersion, Docteur.T("VP.OuAmd"));
            return await amd.DerniereAsync(ct).ConfigureAwait(false) is { } derniere
                ? Juger("AMD", installee, [derniere], listeComplete: false)
                : SansReponse("AMD", installee, Docteur.T("VP.OuAmd"));
        }
        if (cartes.FirstOrDefault(c => c.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) is { } intel)
            // Relevé le 03/10/2026 : la page de téléchargement d'Intel répond 403 à tout programme, même avec les en-têtes
            // d'un navigateur. Pas de source lisible : l'endroit où vérifier est dit.
            return SansSource("Intel", intel.DriverVersion, Docteur.T("VP.OuIntel"));
        return null;
    }

    /// <summary>
    /// La source lue est celle des GeForce récentes. NVIDIA a arrêté les nouvelles
    /// versions pour Maxwell, Pascal et Volta à la branche 580 (annonce de juillet 2025, reprise par Tom's Hardware et
    /// TechPowerUp) : une GTX 1060 se verrait dire « en retard » sur des versions qui ne la prennent pas en charge. Les
    /// cartes professionnelles (Quadro, RTX A) suivent une autre suite. Seules les GeForce qui reçoivent la même version
    /// Game Ready que la RTX 40 de référence sont donc comparées : RTX 20 à 50, GTX 16, MX 400 et 500.
    /// </summary>
    internal static bool SuitLesPilotesGameReady(string modele) =>
        System.Text.RegularExpressions.Regex.IsMatch(modele, @"GeForce\s+(RTX\s+[2-5]0\d0|GTX\s+16\d0|MX\s*[45]\d0)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Les Radeon qui reçoivent la version d'Adrenalin publiée en tête de la page d'AMD (RDNA, RX 5000 à 9000).</summary>
    internal static bool SuitAdrenalin(string modele) =>
        System.Text.RegularExpressions.Regex.IsMatch(modele, @"Radeon\s+RX\s+[5-9]\d{3}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>La version AMD installée au format d'AMD, que Windows garde dans la clé de la carte graphique
    /// (valeur « RadeonSoftwareVersion », relevée dans des exports de registre publiés sur le forum Guru3D) ; <c>null</c>
    /// sans carte AMD ou sans cette valeur. Les clés des cartes retirées restent ; seule celle dont la description
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
    /// <param name="listeComplete">Faux quand la source ne donne que la dernière version (AMD) ; le retard n'est alors
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
                Docteur.T("VP.AJour", marque, installee));
        var derniere = plusRecentes[0];
        var date = derniere.Sortie is { } d ? Docteur.T("VP.Du", d) : "";
        var retard = !listeComplete ? Docteur.T("VP.PasLeDernier")
            : plusRecentes.Count == 1 ? Docteur.T("VP.UneVersion") : Docteur.T("VP.Versions", plusRecentes.Count);
        // Être en retard n'est pas un défaut ; observé, avec son geste (Recommandation).
        return new RepairFinding(Domaine, Nom, $"{marque} {installee}", derniere.Version, RepairStatus.Observed,
            Docteur.T("VP.Retard", marque, installee, retard, derniere.Version, date),
            RepairAction.Recommandation, Remede: Docteur.T("VP.Remede", marque, derniere.Version));
    }

    /// <summary>La source existe mais n'a pas répondu cette fois.</summary>
    public static RepairFinding SansReponse(string marque, string installee, string ouRegarder) =>
        new(Domaine, Nom, $"{marque} {installee}", null, RepairStatus.Unknown,
            Docteur.T("VP.SansReponse", marque, installee, ouRegarder));

    /// <summary>Pas de source lisible pour cette marque (AMD, Intel) : le dire, et où regarder.</summary>
    public static RepairFinding SansSource(string marque, string installee, string ouRegarder) =>
        new(Domaine, Nom, $"{marque} {installee}", null, RepairStatus.Observed,
            Docteur.T("VP.SansSource", marque, ouRegarder));
}

/// <summary>
/// La page de téléchargement d'AMD. Son bouton « Download Windows Drivers » pointe sur l'installeur de la
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

    /// <summary>L'en-tête d'un navigateur : la page d'AMD en veut un. Il ne nomme pas DrDXT, rien ne dit à AMD qui demande.</summary>
    internal const string EnTeteNavigateur = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    private static async Task<string> LireEnLigne(string url, CancellationToken ct)
    {
        using var http = PageDuFabricant.Client();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(EnTeteNavigateur);
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
/// de bureau d'une époque ; la série RTX 40 (psid 129, pfid 1004) sert de référence. L'une des deux requêtes réseau de ce
/// module, avec <see cref="SourceAmd"/>.
/// </summary>
public sealed class SourceNvidia
{
    private const string Url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php"
        + "?func=DriverManualLookup&psid=129&pfid=1004&osID=135&languageCode=1033&isWHQL=1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=5";

    private readonly Func<string, CancellationToken, Task<string>> _lire;

    public SourceNvidia(Func<string, CancellationToken, Task<string>>? lire = null) => _lire = lire ?? LireEnLigne;

    private static async Task<string> LireEnLigne(string url, CancellationToken ct)
    {
        using var http = PageDuFabricant.Client();
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

/// <summary>La lecture d'une page de fabricant : 10 s au plus, et 4 Mio au plus. La réponse de NVIDIA fait 25 Ko (relevé le
/// 03/10/2026) ; une page captive ou un incident côté fabricant qui rendrait des Mio n'est pas lu, la version reste non lue.</summary>
internal static class PageDuFabricant
{
    public const int OctetsMax = 4 * 1024 * 1024;

    public static HttpClient Client(HttpMessageHandler? transport = null)
        => new(transport ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = OctetsMax };
}
