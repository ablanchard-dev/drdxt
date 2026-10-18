using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Updates;

/// <summary>
/// Les tables de DrDXT (radar des régressions connues) mises à jour sans changer de binaire. Le paquet ne porte que des
/// données JSON au schéma fermé (<see cref="TablesDeDonnees.Lire"/>), signées par la même clé que les mises à jour de
/// l'App (<see cref="UpdateTrust"/>) : aucune nouvelle clé, et la clé de dev reste refusée. Les octets signés commencent
/// par <see cref="Domaine"/>, si bien qu'une signature de manifeste de l'App ne vaut jamais pour des données, ni l'inverse.
/// La version est dans les octets signés : un paquet plus ancien que les tables en service est refusé, sans compteur
/// local à falsifier. Le paquet précédent est gardé à côté, et repris si le courant ne se vérifie plus.
/// </summary>
public static class DonneesSignees
{
    public const string Fichier = "donnees.json";
    public const string FichierPrecedent = "donnees-precedentes.json";

    /// <summary>Le chemin du paquet sous la racine du serveur de mise à jour (<c>VerificationMiseAJour.UrlParDefaut</c>).</summary>
    public const string Chemin = "/donnees/paquet.json";

    /// <summary>Un paquet de tables tient en quelques dizaines de kilo-octets ; au-delà, il n'est pas lu.</summary>
    public const int OctetsMax = 1024 * 1024;

    /// <summary>Le préfixe des octets signés. Un manifeste de l'App commence par « { » : aucune signature ne passe d'un
    /// canal à l'autre.</summary>
    public static ReadOnlySpan<byte> Domaine => "DrDXT donnees 1\n"u8;

    /// <summary>Le paquet tel qu'il voyage : sa version, son contenu (le JSON des tables, en texte, signé tel quel) et la
    /// signature Ed25519 en base64.</summary>
    public sealed record Paquet(int Version, string Contenu, string Signature);

    /// <summary>Les octets que la clé de release signe et que l'App vérifie.</summary>
    public static byte[] OctetsSignes(int version, string contenu)
        => [.. Domaine, .. Encoding.ASCII.GetBytes(version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"),
            .. Encoding.UTF8.GetBytes(contenu)];

    /// <summary>Pur : le paquet lu, exactement trois champs, rien d'autre.</summary>
    public static Result<Paquet> Lire(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Refus("the package is not an object");
            var noms = r.EnumerateObject().Select(p => p.Name).ToList();
            if (noms.Count != 3 || !noms.Contains("Version") || !noms.Contains("Contenu") || !noms.Contains("Signature"))
                return Refus("the package carries Version, Contenu and Signature, nothing else");
            if (r.GetProperty("Version").ValueKind != JsonValueKind.Number || !r.GetProperty("Version").TryGetInt32(out var v) || v < 1)
                return Refus("version missing or malformed");
            if (r.GetProperty("Contenu").GetString() is not { } contenu || r.GetProperty("Signature").GetString() is not { } signature)
                return Refus("content or signature missing");
            return Result<Paquet>.Ok(new Paquet(v, contenu, signature));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return Refus("unreadable package: " + ex.Message);
        }
    }

    /// <summary>Le JSON du paquet, tel que l'outil de release l'écrit.</summary>
    public static string Ecrire(Paquet p) => JsonSerializer.Serialize(new { p.Version, p.Contenu, p.Signature });

    /// <summary>La vérification : la clé de release, ou rien (une clé absente ou de dev ne monte aucun vérificateur).</summary>
    public sealed class Verificateur
    {
        private readonly PublicKey _cle;
        private Verificateur(PublicKey cle) => _cle = cle;

        /// <summary>Le vérificateur de production : la clé embarquée de <see cref="UpdateTrust"/>, avec ses refus.</summary>
        public static Result<Verificateur> DeProduction() => Pour(UpdateTrust.ProductionPublicKey);

        public static Result<Verificateur> Pour(byte[]? clePublique)
        {
            var refus = UpdateTrust.Refuser(clePublique);
            return refus.IsOk ? Importer(clePublique!) : Result<Verificateur>.Fail(refus.Error!.Value.Code, refus.Error.Value.Message);
        }

        /// <summary>Pour les tests : une clé que la production refuse (celle de dev), importée sans ses refus.</summary>
        internal static Result<Verificateur> Importer(ReadOnlySpan<byte> clePublique)
        {
            try { return Result<Verificateur>.Ok(new Verificateur(PublicKey.Import(SignatureAlgorithm.Ed25519, clePublique, KeyBlobFormat.RawPublicKey))); }
            catch (Exception ex) { return Result<Verificateur>.Fail(UpdateErrorCodes.SignatureInvalid, "unreadable key: " + ex.Message); }
        }

        /// <summary>Signature d'abord, puis version contre le plancher, puis schéma. Le moindre doute refuse tout.</summary>
        public Result<TablesDeDonnees> Verifier(Paquet p, int plancher)
        {
            byte[] signature;
            try { signature = Convert.FromBase64String(p.Signature); }
            catch (FormatException) { return Result<TablesDeDonnees>.Fail(UpdateErrorCodes.SignatureInvalid, "signature is not base64"); }
            if (signature.Length != SignatureAlgorithm.Ed25519.SignatureSize)
                return Result<TablesDeDonnees>.Fail(UpdateErrorCodes.SignatureInvalid, "malformed signature");
            bool valide;
            try { valide = SignatureAlgorithm.Ed25519.Verify(_cle, OctetsSignes(p.Version, p.Contenu), signature); }
            catch (Exception) { valide = false; }
            if (!valide) return Result<TablesDeDonnees>.Fail(UpdateErrorCodes.SignatureInvalid, "the signature does not verify");
            if (p.Version < plancher)
                return Result<TablesDeDonnees>.Fail(UpdateErrorCodes.Downgrade, $"version {p.Version} is older than {plancher}");
            return TablesDeDonnees.Lire(p.Contenu, p.Version);
        }
    }

    /// <summary>Au démarrage : le paquet gardé s'il se vérifie, sinon le précédent, sinon les tables embarquées. Le
    /// plancher est la version embarquée : rien de plus ancien que ce que l'App connaît déjà.</summary>
    public static TablesDeDonnees Charger(string dossier, Verificateur verificateur)
    {
        foreach (var nom in new[] { Fichier, FichierPrecedent })
        {
            try
            {
                var chemin = Path.Combine(dossier, nom);
                if (!File.Exists(chemin) || new FileInfo(chemin).Length > OctetsMax) continue;
                var lu = Lire(File.ReadAllText(chemin));
                if (!lu.IsOk) continue;
                var tables = verificateur.Verifier(lu.Value!, TablesDeDonnees.Embarquees.Version);
                if (tables.IsOk) return tables.Value!;
            }
            catch (Exception) { /* fichier illisible : le suivant, puis les tables embarquées */ }
        }
        return TablesDeDonnees.Embarquees;
    }

    /// <summary>Ce que la demande au serveur a donné.</summary>
    public enum Issue { RienDeNeuf, Installe, Refuse, Injoignable, Echec }

    /// <summary>Demande le paquet au serveur. Un paquet plus récent que <paramref name="courantes"/> et vérifié est gardé
    /// (l'ancien devient le précédent) puis rendu ; tout le reste laisse les tables en service telles quelles.</summary>
    public static async Task<(Issue Issue, TablesDeDonnees? Tables)> DemanderAsync(
        HttpClient client, Uri racine, string dossier, Verificateur verificateur, TablesDeDonnees courantes, CancellationToken ct)
    {
        string json;
        try
        {
            var url = racine.GetLeftPart(UriPartial.Path).TrimEnd('/') + Chemin;
            using var reponse = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Rien de publié encore : joignable, rien à offrir.
            if (reponse.StatusCode == HttpStatusCode.NotFound) return (Issue.RienDeNeuf, null);
            if (!reponse.IsSuccessStatusCode) return (Issue.Injoignable, null);
            if (reponse.Content.Headers.ContentLength > OctetsMax) return (Issue.Refuse, null);
            // Lu par morceaux et arrêté au plafond : un corps sans longueur annoncée n'est jamais gardé en entier.
            await using var flux = await reponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var lus = new MemoryStream();
            var tampon = new byte[64 * 1024];
            int n;
            while ((n = await flux.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
            {
                if (lus.Length + n > OctetsMax) return (Issue.Refuse, null);
                lus.Write(tampon, 0, n);
            }
            json = Encoding.UTF8.GetString(lus.GetBuffer(), 0, (int)lus.Length);
        }
        catch (OperationCanceledException) { return (Issue.Injoignable, null); }
        catch (HttpRequestException) { return (Issue.Injoignable, null); }

        var paquet = Lire(json);
        if (!paquet.IsOk) return (Issue.Refuse, null);
        if (paquet.Value!.Version == courantes.Version) return (Issue.RienDeNeuf, null);
        var tables = verificateur.Verifier(paquet.Value, courantes.Version + 1);
        if (!tables.IsOk) return (Issue.Refuse, null);

        try
        {
            Directory.CreateDirectory(dossier);
            var chemin = Path.Combine(dossier, Fichier);
            if (File.Exists(chemin)) File.Copy(chemin, Path.Combine(dossier, FichierPrecedent), overwrite: true);
            var ecrit = new FichierJsonAtomique(chemin, ErrorCodes.StorageFail).WriteAtomic(json);
            return ecrit.IsOk ? (Issue.Installe, tables.Value) : (Issue.Echec, tables.Value);
        }
        catch (Exception) { return (Issue.Echec, tables.Value); }
    }

    private static Result<Paquet> Refus(string pourquoi) => Result<Paquet>.Fail(ErrorCodes.DonneesHorsSchema, pourquoi);
}
