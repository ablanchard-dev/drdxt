using System.Security.Cryptography;
using System.Text;

namespace DrDxt.ElevatedHelper.Ipc;

// HMAC-SHA256 sur les octets canoniques d'une requête privilégiée typée : l'intégrité du transport seulement (le JSON de
// l'op + le nonce intacts, le pair détient la clé de session), ni l'autorisation (ICallerAuthenticator) ni l'allowlist
// (OpAllowlist). Vérification en temps constant (FixedTimeEquals), sans levée sur une entrée malformée (fail-closed).
// Clé injectée : une clé HMAC éphémère par session.
public static class TypedRequestHmac
{
    // Séparateur d'unité ASCII (0x1F) entre les champs. La forme reste sans ambiguïté parce que le séparateur ne peut
    // jamais apparaître brut dans un champ : l'op sort du codec, dont le JSON échappe les caractères de contrôle, et le
    // nonce est un GUID en hexadécimal (LaFormeSigneeResteSansAmbiguiteTests). Écrit en échappement, pour qu'aucun
    // copier-coller ni éditeur ne l'efface.
    private const string FieldSeparator = "\u001F";

    public static byte[] CanonicalBytes(string opJson, string nonce)
    {
        var canonical = $"typed-v1{FieldSeparator}{opJson}{FieldSeparator}{nonce}";
        return Encoding.UTF8.GetBytes(canonical);
    }

    public static string Compute(ReadOnlySpan<byte> key, string opJson, string nonce)
    {
        var data = CanonicalBytes(opJson, nonce);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var written = HMACSHA256.HashData(key, data, mac);
        return Convert.ToBase64String(mac[..written]);
    }

    public static bool Verify(ReadOnlySpan<byte> key, string opJson, string nonce, string? hmacBase64)
    {
        // Garde avant le décodage : un HMAC null ou vide doit refuser (fail-closed), jamais lever.
        if (string.IsNullOrEmpty(hmacBase64))
            return false;
        if (opJson is null || nonce is null)
            return false;

        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(hmacBase64);
        }
        catch (FormatException)
        {
            return false; // base64 malformé → fail-closed
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var data = CanonicalBytes(opJson, nonce);
        HMACSHA256.HashData(key, data, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
