using System.Text.Json;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// CompositeSnapshotCodec : enveloppe plusieurs PrivilegedOp inverses dans un seul snapshot.
//
// Un levier qui règle plusieurs choses à la fois (par exemple le plafond VRR : Vsync + plafond
// d'images ; Service + CppcEnable + Game Bar) doit toutes les rendre au retour arrière. Chaque
// élément est lui-même encodé par le PrivilegedOpCodec à la feuille près : le modèle de
// sécurité ne change pas, ceci ne fait qu'encadrer une liste ordonnée de ces formes filaires
// dans une seule chaîne de snapshot.
//
// Le décodage est total : un format malformé, qui n'est pas un tableau, ou un élément
// indécodable rend un Result en échec ; jamais d'exception, jamais de type arbitraire lié.
//
// Vit dans Core pour que les leviers et le mode désinstallation du helper élevé lisent le
// même format de snapshot composite : le helper référence Core, jamais
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
