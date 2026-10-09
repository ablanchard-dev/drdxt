using System.Net.NetworkInformation;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 07/10/2026 (T1, kit testeur) : une machine au format de tests/Shared/machines (Nom, Note, Eleve, Inventaire, Reglages,
/// Sante), anonymisée, pour qu'un fichier reçu d'un joueur devienne une fixture tel quel. Un seul format, partagé par le
/// bouton « Exporter ma machine » de l'App et par tools/enregistreur. Rien ne part sur le réseau : c'est le joueur qui
/// envoie le fichier.
/// </summary>
public static class MachineExportee
{
    /// <summary>
    /// Ce qui est retiré de chaque texte du fichier, dans cet ordre. Une ligne ajoutée ici sans valeur semée dans
    /// ExportDeMachineTests fait échouer ce test ; docs/TESTEUR-EXPORTER-MA-MACHINE.md en recopie la liste.
    /// </summary>
    public static readonly IReadOnlyList<string> CeQuiEstRetire =
    [
        @"le dossier de n'importe quel compte (X:\Users\<nom>) : il devient X:\Users\joueur, la suite du chemin reste",
        "le nom du compte Windows (même de deux lettres), le nom du PC et son domaine",
        "les numéros de série et identifiants matériels lus sur ce PC (BIOS, carte mère, disques, mémoire, UUID de la machine, numéros de volume)",
        "les adresses MAC (avec tirets, deux-points ou collées, pour celles des cartes de ce PC)",
        "les adresses IP (v4 et v6) ; une version à quatre nombres (1.4.6.3) a la forme d'une adresse v4 et part aussi, sauf dans les champs de version (DriverVersion)",
        "les identifiants de compte Windows (SID S-1-5-21-…)",
        "les identifiants de volume (Volume{…})",
        "les adresses de courriel",
        "le nom d'organisation des dossiers OneDrive (« OneDrive - <organisation> »)",
    ];

    private const string Retire = "<retiré>";
    private static readonly TimeSpan Delai = TimeSpan.FromMilliseconds(250);

    // Le nom du compte va jusqu'au séparateur suivant ; sans séparateur, jusqu'au premier blanc.
    private static readonly Regex Profil = new(@"(?<tete>[A-Za-z]:[\\/]Users[\\/])(?:[^\\/\r\n]+(?=[\\/])|[^\\/\s]+)",
        RegexOptions.IgnoreCase, Delai);
    private static readonly Regex AdresseMac = new(@"(?<![0-9A-Fa-f:-])[0-9A-Fa-f]{2}(?:([:-])[0-9A-Fa-f]{2})(?:\1[0-9A-Fa-f]{2}){4}(?![0-9A-Fa-f:-])",
        RegexOptions.None, Delai);
    // Quatre octets bornés, hors d'une suite pointée plus longue (une version 1.2.3.4.5 n'est pas une adresse).
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d]|\.\d)",
        RegexOptions.None, Delai);
    // Huit groupes, ou une forme abrégée « :: » : une heure « 18:47:07 » n'en est pas une.
    private static readonly Regex Ipv6 = new(
        @"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?=[0-9A-Fa-f:]*[0-9A-Fa-f])(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?)(?:%\w+)?(?![0-9A-Fa-f:])",
        RegexOptions.None, Delai);
    private static readonly Regex Sid = new(@"\bS-1-5-21(?:-\d+){1,4}\b", RegexOptions.None, Delai);
    private static readonly Regex VolumeGuid = new(@"Volume\{[0-9A-Fa-f-]{36}\}", RegexOptions.IgnoreCase, Delai);
    // 07/10/2026 (revue F-21) : « OneDrive - Nom de l'entreprise » nomme l'employeur du joueur.
    private static readonly Regex OneDrive = new(@"(?<tete>OneDrive - )[^\\/\r\n""]+", RegexOptions.IgnoreCase, Delai);
    private static readonly Regex Courriel = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.None, Delai);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Relue(string Nom, HardwareInventory Inventaire, SettingsState Reglages, List<RepairFinding> Sante);

    /// <param name="valeursDeCePc">Les valeurs propres à ce PC (voir <see cref="ValeursDeCePc"/>), retirées partout.</param>
    /// <returns>Le texte du fichier. Il est relu avant d'être rendu : un fichier qui ne se relirait pas lève ici.</returns>
    public static string Json(string nom, string? note, bool? eleve, HardwareInventory inventaire, SettingsState reglages,
        IReadOnlyList<RepairFinding> sante, IEnumerable<string> valeursDeCePc)
    {
        var propres = valeursDeCePc.Select(v => v?.Trim() ?? "").Where(v => v.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length)
            .Select(v => new Regex($"(?<![A-Za-z0-9]){Regex.Escape(v)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase, Delai)).ToList();
        string Anonymiser(string s, string? cle)
        {
            s = Profil.Replace(s, "${tete}joueur");
            s = OneDrive.Replace(s, "${tete}" + Retire);
            foreach (var p in propres) s = p.Replace(s, Retire);
            s = AdresseMac.Replace(s, Retire);
            if (cle?.EndsWith("Version", StringComparison.Ordinal) != true) s = Ipv4.Replace(s, Retire);
            s = Ipv6.Replace(s, Retire);
            s = Sid.Replace(s, Retire);
            s = VolumeGuid.Replace(s, "Volume{" + Retire + "}");
            return Courriel.Replace(s, Retire);
        }

        var noeud = JsonSerializer.SerializeToNode(new { Nom = nom, Note = note, Eleve = eleve, Inventaire = inventaire,
            Reglages = reglages, Sante = sante }, Options)!;
        Nettoyer(noeud, null, Anonymiser);
        var json = noeud.ToJsonString(Options);
        _ = JsonSerializer.Deserialize<Relue>(json, Options) ?? throw new InvalidDataException("la machine exportée ne se relit pas");
        return json;
    }

    // La clé suit le texte (un élément de liste garde celle de sa liste) : une version n'est pas une adresse.
    private static void Nettoyer(JsonNode? noeud, string? cleParente, Func<string, string?, string> anonymiser)
    {
        switch (noeud)
        {
            case JsonObject o:
                foreach (var cle in o.Select(p => p.Key).ToList())
                    if (Texte(o[cle]) is { } s) o[cle] = anonymiser(s, cle); else Nettoyer(o[cle], cle, anonymiser);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (Texte(a[i]) is { } s) a[i] = anonymiser(s, cleParente); else Nettoyer(a[i], cleParente, anonymiser);
                break;
        }
    }

    private static string? Texte(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Lit sur ce PC les valeurs qui le désignent : compte, nom, domaine, dossier du profil, numéros de série (WMI), adresses
    /// MAC et IP des cartes réseau. Lecture seule ; une source illisible est sautée, le reste des règles s'applique.
    /// </summary>
    public static IReadOnlyList<string> ValeursDeCePc()
    {
        var v = new List<string>();
        void Essayer(Func<IEnumerable<string?>> lire)
        {
            try { v.AddRange(lire().OfType<string>()); }
            catch (Exception) { /* source illisible : sautée */ }
        }
        Essayer(() => [Environment.UserName, Environment.MachineName, Environment.UserDomainName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);
        var wmi = new ManagementWmiQuery();
        foreach (var (classe, propriete) in new[]
                 {
                     ("Win32_BIOS", "SerialNumber"), ("Win32_BaseBoard", "SerialNumber"), ("Win32_DiskDrive", "SerialNumber"),
                     ("Win32_PhysicalMedia", "SerialNumber"), ("Win32_PhysicalMemory", "SerialNumber"),
                     ("Win32_ComputerSystemProduct", "IdentifyingNumber"), ("Win32_ComputerSystemProduct", "UUID"),
                     ("Win32_SystemEnclosure", "SerialNumber"), ("Win32_LogicalDisk", "VolumeSerialNumber"),
                 })
            Essayer(() => wmi.Query($"SELECT {propriete} FROM {classe}") is { IsOk: true } r
                ? r.Value!.Select(l => l.GetString(propriete)) : []);
        Essayer(() => NetworkInterface.GetAllNetworkInterfaces().SelectMany(n =>
        {
            var mac = n.GetPhysicalAddress().ToString();
            return (IEnumerable<string?>)[mac, .. n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString())];
        }));
        return v;
    }
}
