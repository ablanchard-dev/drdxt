using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Tramage JSON préfixé par sa longueur sur le flux du pipe.
//
// Chaque message = un préfixe de longueur de 4 octets big-endian suivi d'exactement autant
// d'octets JSON UTF-8. Le préfixe est borné (MaxMessageBytes) pour qu'un pair hostile ou
// déchet ne puisse pas faire allouer au serveur une mémoire sans borne.
//
// Sécurité : la désérialisation ne vise que des DTO fixes et fermés ; aucune instanciation
// guidée par un nom de type ici.
// -----------------------------------------------------------------------
public static class WireCodec
{
    // Large pour une enveloppe minuscule ; protège d'un préfixe de longueur démesuré.
    public const int MaxMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Rejeter les membres inconnus (fail-closed) : un champ inattendu fait lever à Deserialize
        // une JsonException → ReadMessageAsync rend null → l'appelant la traite comme une
        // mauvaise requête (OP_NOT_ALLOWED). Les enums voyagent par leur valeur entière
        // (le discriminant fermé), jamais par leur nom. Les noms de propriétés respectent la casse.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, json.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lit un message tramé. Rend null si le flux s'est fermé proprement avant toute
    /// donnée, ou si la trame est malformée ou démesurée (fail-closed : l'appelant
    /// traite null comme une mauvaise requête au lieu de planter).
    /// </summary>
    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
        where T : class
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false))
            return null; // EOF propre ou lecture courte sur le préfixe

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > MaxMessageBytes)
            return null; // longueur hors plage → malformée, fail-closed

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null; // truncated payload

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null; // JSON malformé → fail-closed
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (read == 0)
                return false; // EOF avant la lecture complète du tampon
            offset += read;
        }
        return true;
    }
}
