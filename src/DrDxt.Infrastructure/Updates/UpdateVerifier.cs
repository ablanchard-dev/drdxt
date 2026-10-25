using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// La barrière de confiance des mises à jour. Vérifie la signature Ed25519 sur le manifeste
// canonique avec la clé publique reçue (en production, celle d'UpdateTrust, qui refuse la clé de dev),
// puis, et seulement alors, l'empreinte de la charge.
//
// La règle cardinale : échec propre, jamais fail-open. Toute incertitude (clé embarquée corrompue,
// signature de mauvaise longueur, exception NSec, empreinte illisible, version inférieure) se
// réduit à un Fail(UPDATE_*). Aucun chemin ne continue dans le doute ; l'appelant garde la version
// installée actuelle.
//
// Ordre imposé (la signature avant tout le reste) : VerifyManifest (signature) d'abord ;
// l'appelant ne récupère la charge que sur Ok ; VerifyPayload vérifie ensuite le SHA-256 contre
// l'empreinte du manifeste. On ne se fie qu'à une empreinte venue d'un manifeste déjà prouvé signé.
//
// Asymétrie (comme Ed25519ManifestVerifier) : construit à partir de la seule clé publique ; ne
// peut que vérifier, jamais forger un manifeste. La chaîne de publication signe hors bande.
// -----------------------------------------------------------------------

/// <summary>Vérificateur Ed25519 des mises à jour : ne tient que la clé publique de 32 octets.</summary>
public sealed class UpdateVerifier : IUpdateVerifier
{
    private readonly PublicKey _publicKey;

    private UpdateVerifier(PublicKey publicKey) => _publicKey = publicKey;

    /// <summary>Construit un vérificateur à partir d'une clé publique Ed25519 brute de 32 octets.
    /// Échec propre : une clé corrompue ou de mauvaise longueur rend Fail(UPDATE_SIG_INVALID) au lieu
    /// de lever, pour qu'un futur mauvais échange de clé ne fasse jamais planter la barrière.</summary>
    public static Result<IUpdateVerifier> Create(ReadOnlySpan<byte> publicKey)
    {
        try
        {
            var imported = PublicKey.Import(
                SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
            return Result<IUpdateVerifier>.Ok(new UpdateVerifier(imported));
        }
        catch (Exception ex)
        {
            return Result<IUpdateVerifier>.Fail(
                UpdateErrorCodes.SignatureInvalid,
                $"Embedded update public key could not be imported: {ex.Message}");
        }
    }

    private const int SignatureEnBase64Max = 128;

    public Result VerifyManifest(UpdateManifest manifest)
    {
        if (manifest is null)
            return Result.Fail(UpdateErrorCodes.SignatureInvalid, "Update manifest is absent.");

        // Décoder la signature détachée. Absente / vide / non base64 / mauvaise longueur →
        // refus. Jamais « ok par défaut ».
        byte[] signature;
        try
        {
            if (string.IsNullOrEmpty(manifest.Signature))
                return Result.Fail(
                    UpdateErrorCodes.SignatureInvalid, "Update manifest signature is absent.");
            // 64 octets font 88 caractères en base64 : une signature plus longue n'est pas décodée.
            if (manifest.Signature.Length > SignatureEnBase64Max)
                return Result.Fail(
                    UpdateErrorCodes.SignatureInvalid, "Update manifest signature is malformed.");
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature is not valid base64.");
        }

        if (signature.Length != SignatureAlgorithm.Ed25519.SignatureSize)
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature is malformed.");

        try
        {
            var canonical = CanonicalBytes(manifest);
            // NSec.Verify rend false sur toute signature mauvaise ou forgée → Result en échec propre.
            return SignatureAlgorithm.Ed25519.Verify(_publicKey, canonical, signature)
                ? Result.Ok()
                : Result.Fail(
                    UpdateErrorCodes.SignatureInvalid, "Update manifest signature did not verify.");
        }
        catch (Exception)
        {
            // Frontière crypto emballée : toute panne inattendue est un refus, jamais un passage.
            return Result.Fail(
                UpdateErrorCodes.SignatureInvalid, "Update manifest signature verification faulted.");
        }
    }

    public Result VerifyPayload(
        UpdateManifest manifest, byte[] payload, string installedVersion, bool allowDowngrade)
    {
        if (manifest is null)
            return Result.Fail(UpdateErrorCodes.SignatureInvalid, "Update manifest is absent.");
        if (payload is null)
            return Result.Fail(UpdateErrorCodes.HashMismatch, "Update payload is absent.");

        // Règle de non-rétrogradation, imposée avant de valider une charge. Une version inférieure (hors
        // canal bêta qui l'autorise) est refusée : jamais de retour en arrière silencieux.
        if (!allowDowngrade && IsDowngrade(manifest.Version, installedVersion))
            return Result.Fail(
                UpdateErrorCodes.Downgrade,
                $"Update version {manifest.Version} is older than installed {installedVersion}.");

        // L'empreinte de comparaison vient d'un manifeste que l'appelant a déjà prouvé signé
        // (VerifyManifest doit tourner d'abord). Calculer le SHA-256 des octets et le comparer à
        // l'empreinte fiable du manifeste. Désaccord → charge jamais exécutée.
        string actual;
        try
        {
            actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return Result.Fail(UpdateErrorCodes.HashMismatch, "Update payload hash could not be computed.");
        }

        var expected = (manifest.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        // Comparaison ordinale, longueur vérifiée, casse normalisée. (Ce sont des empreintes hexadécimales
        // publiques, pas un secret : pas d'exigence de temps constant.)
        if (expected.Length == 0 || !string.Equals(actual, expected, StringComparison.Ordinal))
            return Result.Fail(
                UpdateErrorCodes.HashMismatch,
                "Downloaded update payload SHA-256 does not match the signed manifest.");

        return Result.Ok();
    }

    // -------------------------------------------------------------------
    // Comparaison de versions : vrai si et seulement si `candidate` est strictement plus ancienne que
    // `installed`. Comparaison numérique par points du cœur (1.2.10 > 1.2.9), puis, à cœur égal, une préversion avant
    // sa version stable ; la métadonnée de build ne compte pas. Tout composant illisible vaut 0,
    // pour qu'une version malformée ne passe jamais en silence pour « plus récente » ; une version
    // égale n'est pas une rétrogradation (reproposer la même version est sans effet en amont, pas un
    // retour en arrière). L'analyse ne lève pas (int.TryParse, null toléré).
    // -------------------------------------------------------------------
    internal static bool IsDowngrade(string candidate, string installed)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(installed);
        var len = Math.Max(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            if (ai < bi) return true;   // strictement plus ancienne → rétrogradation
            if (ai > bi) return false;  // strictement plus récente → pas une rétrogradation
        }
        // Cœur égal : une préversion (« -rc.1 ») vient avant sa version stable, jamais après.
        return EstUnePreversion(candidate) && !EstUnePreversion(installed);
    }

    private static bool EstUnePreversion(string? version)
        => version is not null && StripMetadata(version).Contains('-');

    // La métadonnée de build (« +abc1234 ») ne compte jamais dans l'ordre.
    private static string StripMetadata(string version)
        => version.Trim() is var v && v.IndexOf('+') is var plus and >= 0 ? v[..plus] : v.Trim();

    private static int[] ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new[] { 0 };
        // Seul le cœur numérique (avant « - » ou « + ») se compare : « 1.0.0-rc.1 » ne se lit plus 1.0.0.1.
        var coeur = StripMetadata(version);
        if (coeur.IndexOf('-') is var tiret and >= 0) coeur = coeur[..tiret];
        var parts = coeur.Split('.');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            nums[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return nums;
    }

    // -------------------------------------------------------------------
    // Sérialisation canonique des champs de confiance du manifeste : une forme d'octets déterministe
    // pour que la signature soit calculée et vérifiée sur exactement les mêmes octets des deux côtés.
    // Le champ de signature lui-même ne fait pas partie des octets signés (c'est la signature) :
    //   * ordre des champs fixe (channel, version, url, sha256), écrit explicitement ;
    //   * le canal par son nom d'enum invariant (stable d'un runtime / d'une culture à l'autre) ;
    //   * aucune indentation ni mise en forme dépendant de la culture ; encodeur détendu (sans
    //     échappement en trop), puisque ces octets ne sont que signés / vérifiés, jamais insérés en HTML.
    // -------------------------------------------------------------------
    // Public, pour que l'outil de release (tools/publication/signer.cs) signe exactement les octets que
    // l'App vérifie, sans recopier la forme canonique.
    public static byte[] CanonicalBytes(UpdateManifest manifest)
        => Encoding.UTF8.GetBytes(Canonicalize(manifest));

    /// <summary>La chaîne JSON canonique et déterministe sur laquelle la signature est calculée.
    /// Exposée aux tests (InternalsVisibleTo) pour qu'une donnée d'essai signe les octets exacts et que
    /// la forme canonique elle-même soit figée.</summary>
    internal static string Canonicalize(UpdateManifest manifest)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("channel", manifest.Channel.ToString());
            writer.WriteString("version", manifest.Version);
            writer.WriteString("url", manifest.Url);
            writer.WriteString("sha256", manifest.Sha256);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
