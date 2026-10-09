using System.Text.Json;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// CompositeSnapshotCodec — wraps MULTIPLE inverse PrivilegedOps in one snapshot.
//
// A tweak that configures several things at once (e.g. D3: Vsync + frame cap;
// F1: service + CppcEnable + Game Bar) must restore ALL of them on revert. Each
// element is itself encoded by the leaf-exact PrivilegedOpCodec, so the security
// model is unchanged — this only frames an ordered list of those wire forms inside
// one snapshot string.
//
// Decode is TOTAL: a malformed / non-array / element-decode failure returns a
// failed Result; it never throws and never binds an arbitrary type.
//
// Lives in Core (11/09/2026, moved from Application) so the tweaks AND the elevated helper's
// uninstall mode read the same composite-snapshot format — the helper references Core, never
// Application.
// -----------------------------------------------------------------------
public static class CompositeSnapshotCodec
{
    private const string OpsProperty = "ops";

    public static Result<string> Encode(IReadOnlyList<PrivilegedOp> inverseOps)
    {
        var wires = new List<string>(inverseOps.Count);
        foreach (var op in inverseOps)
        {
            var w = PrivilegedOpCodec.Serialize(op);
            if (!w.IsOk || w.Value is null)
                return Result<string>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize a revert op.");
            wires.Add(w.Value);
        }

        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray(OpsProperty);
                foreach (var wire in wires)
                {
                    // Each element is already a JSON object (a codec wire form): embed it verbatim.
                    using var elem = JsonDocument.Parse(wire);
                    elem.RootElement.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Result<string>.Ok(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ErrorCodes.TweakSdkUnavailable, $"Composite encode error: {ex.Message}");
        }
    }

    public static Result<IReadOnlyList<PrivilegedOp>> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<IReadOnlyList<PrivilegedOp>>.Fail(ErrorCodes.SnapshotInvalid, "Empty composite snapshot.");

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(OpsProperty, out var ops)
                || ops.ValueKind != JsonValueKind.Array)
            {
                return Result<IReadOnlyList<PrivilegedOp>>.Fail(
                    ErrorCodes.SnapshotInvalid, "Composite snapshot is not a { ops: [...] } object.");
            }

            var decoded = new List<PrivilegedOp>();
            foreach (var elem in ops.EnumerateArray())
            {
                var one = PrivilegedOpCodec.Deserialize(elem.GetRawText());
                if (!one.IsOk || one.Value is null)
                    return Result<IReadOnlyList<PrivilegedOp>>.Fail(
                        ErrorCodes.SnapshotInvalid, "A composite snapshot element is not a known typed op.");
                decoded.Add(one.Value);
            }

            return Result<IReadOnlyList<PrivilegedOp>>.Ok(decoded);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<PrivilegedOp>>.Fail(ErrorCodes.SnapshotInvalid, $"Composite decode error: {ex.Message}");
        }
        finally
        {
            doc?.Dispose();
        }
    }
}
