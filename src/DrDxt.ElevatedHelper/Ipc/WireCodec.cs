using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Length-prefixed JSON framing over the pipe stream.
//
// Each message = a 4-byte big-endian length prefix followed by exactly that
// many UTF-8 JSON bytes. A length prefix is bounded (MaxMessageBytes) so a
// hostile/garbage peer cannot make the server allocate unbounded memory.
//
// SECURITY: deserialization is into FIXED, closed DTOs only. OpKind is an enum
// (closed discriminator); there is no type-name-driven instantiation here.
// -----------------------------------------------------------------------
public static class WireCodec
{
    // Generous for a tiny envelope; defends against an oversized length prefix.
    public const int MaxMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Reject unknown members fail-closed: an unexpected field makes Deserialize
        // throw JsonException → ReadMessageAsync returns null → the caller treats it
        // as a bad request (OP_NOT_ALLOWED). Enums travel as their integer value
        // (the closed discriminator), never by name. Property names are case-sensitive.
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
    /// Reads one framed message. Returns null if the stream closed cleanly
    /// before any data, or if the frame is malformed/oversized (fail-closed:
    /// the caller treats null as a bad request rather than crashing).
    /// </summary>
    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
        where T : class
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false))
            return null; // clean EOF or short read on the prefix

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > MaxMessageBytes)
            return null; // out-of-range length → malformed, fail-closed

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null; // truncated payload

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null; // malformed JSON → fail-closed
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
                return false; // EOF before the full buffer was read
            offset += read;
        }
        return true;
    }
}
