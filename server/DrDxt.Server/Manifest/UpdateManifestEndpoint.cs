using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DrDxt.Server.Common;

namespace DrDxt.Server.Manifest;

// -----------------------------------------------------------------------
// LU — la route qui SERT le manifeste de mise a jour, signee.
//
// Le client porte depuis M5 tout le moteur (AutoUpdater, IUpdateVerifier, la clef publique
// embarquee) et n'avait aucune source a interroger : le serveur n'exposait que le webhook et
// /health, et le signeur de manifeste, pourtant enregistre, n'etait appele par personne.
//
// DUPLICATION ASSUMEE. La forme canonique ci-dessous doit produire les MEMES octets que
// `DrDxt.Infrastructure.Updates.UpdateVerifier.Canonicalize` cote client. Les deux ne peuvent pas
// partager de code : le client cible `net8.0-windows`, ce serveur cible `net8.0`. Une copie derive
// toujours — celle-ci est donc gardee par un test qui fait signer ICI et verifier LA-BAS avec la
// clef publique reellement embarquee dans l'application. Le jour ou l'un des deux change un nom de
// champ ou leur ordre, ce test rougit ; aucune relecture humaine n'est demandee.
// -----------------------------------------------------------------------
public static class UpdateManifestEndpoint
{
    // Liste FERMEE des canaux, dans la casse exacte que le client canonise (le nom INVARIANT de son
    // enum UpdateChannel). Le segment d'url est libre de casse ; ce qui est signe ne l'est pas.
    private static readonly Dictionary<string, string> Channels =
        new(StringComparer.OrdinalIgnoreCase) { ["app"] = "App", ["profiles"] = "Profiles" };

    public static IResult Handle(string channel, IConfiguration config, IManifestSigningService signer)
    {
        if (!Channels.TryGetValue(channel ?? string.Empty, out var canonicalChannel))
        {
            return Results.NotFound();
        }

        var section = config.GetSection("Updates:" + canonicalChannel);
        var version = section["Version"];
        var url = section["Url"];
        var sha256 = section["Sha256"];

        // Rien de publie sur ce canal. Ce n'est PAS une erreur, et c'est l'etat reel aujourd'hui :
        // le client doit lire « joignable, rien a offrir » et rester sur sa version. Un 404 ici lui
        // ferait conclure a une panne de la mise a jour.
        if (string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(sha256))
        {
            return Results.NoContent();
        }

        // 19/09/2026 — CE QUI NE SE VERIFIE PAS NE SE SIGNE PAS.
        //
        // Ces trois valeurs viennent de la configuration et partaient SIGNEES sans qu'on les
        // regarde : une url en `http://`, un sha256 tronque, un `TODO` oublie. Or la signature
        // est exactement ce qui fait la confiance du client — il embarque la clef publique et
        // n'accepte que ce qui est signe. Signer une valeur non verifiee transforme donc une
        // faute de frappe en INSTRUCTION DE CONFIANCE.
        //
        // Un manifeste douteux prend le chemin deja ecrit pour un manifeste non signable :
        // 503, fail-closed. Deplacer la panne chez le client sans la nommer serait pire.
        if (!EstPubliable(url, sha256))
        {
            return Results.StatusCode(503);
        }

        var signature = signer.Sign(CanonicalBytes(canonicalChannel, version, url, sha256));
        if (!signature.IsOk)
        {
            // Fail-closed. Un manifeste non signe serait refuse par le client de toute facon :
            // le servir quand meme ne ferait que deplacer la panne chez lui, sans la nommer.
            return Results.StatusCode(503);
        }

        return Results.Json(new
        {
            channel = canonicalChannel,
            version,
            url,
            sha256,
            signature = signature.Value,
        });
    }

    /// <summary>Un manifeste est-il assez sain pour porter notre sceau ?
    ///
    /// Deux regles, chacune avec sa consequence mesuree :
    ///   * l'adresse doit etre une URL ABSOLUE en HTTPS. Le sha256 signe protege le contenu,
    ///     mais rien n'oblige un reseau hostile a servir CE contenu : en clair, l'utilisateur
    ///     voit une mise a jour qui echoue sans raison dicible ;
    ///   * le sha256 doit etre 64 caracteres hexadecimaux. Autrement le client refusera
    ///     TOUJOURS la mise a jour, en silence, et le produit cessera de se mettre a jour sans
    ///     que personne ne l'apprenne. C'est la panne la plus discrete des deux.
    /// </summary>
    private static bool EstPubliable(string url, string sha256)
    {
        var adresseSaine = Uri.TryCreate(url, UriKind.Absolute, out var adresse)
                           && adresse.Scheme == Uri.UriSchemeHttps;

        var empreinteSaine = sha256.Length == 64
                             && sha256.All(Uri.IsHexDigit);

        return adresseSaine && empreinteSaine;
    }

    // Les quatre champs de confiance, dans un ordre IMPOSE, sans indentation, encodeur relaxe.
    // La signature ne porte jamais sur elle-meme.
    public static byte[] CanonicalBytes(string channel, string version, string url, string sha256)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("channel", channel);
            writer.WriteString("version", version);
            writer.WriteString("url", url);
            writer.WriteString("sha256", sha256);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
