using System.Text.Json;

namespace DrDxt.Core;

// CompositeSnapshotCodec : plusieurs PrivilegedOp inverses dans un seul snapshot, pour un levier qui règle plusieurs
// choses (le plafond VRR : Vsync + plafond d'images ; Service + CppcEnable + Game Bar). Chaque élément reste encodé par
// PrivilegedOpCodec à la feuille près : une liste ordonnée de formes filaires, même modèle de sécurité. Décodage total :
// malformé, pas un tableau ou élément indécodable → Result en échec, jamais d'exception ni de type arbitraire. Dans
// Core, pour que le mode désinstallation du helper (qui référence Core, jamais Application) lise le même format.
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
                    // Chaque élément est déjà un objet JSON (une forme filaire du codec) : on l'insère tel quel.
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
