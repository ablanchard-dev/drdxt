using DrDxt.Core;

namespace DrDxt.Detection.Wmi;

/// <summary>Une ligne WMI = dictionnaire propriété→valeur, accès défensif typé.</summary>
public sealed record WmiRow(IReadOnlyDictionary<string, object?> Properties)
{
    public string? GetString(string key) =>
        Properties.TryGetValue(key, out var v) ? v?.ToString()?.Trim() : null;

    public int? GetInt(string key)
    {
        if (!Properties.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            int i => i,
            // WMI rend les UInt16 (BusType, MediaType…) et les UInt8 tels quels ; sans ces deux lignes,
            // toute propriété de ces types passerait pour « absente ».
            ushort us => us,
            short sh => sh,
            byte by => by,
            sbyte sb => sb,
            uint u => (int)u,
            long l => (int)l,
            ulong ul => (int)ul,
            string s when int.TryParse(s, out var p) => p,
            _ => null
        };
    }

    public ulong? GetUlong(string key)
    {
        if (!Properties.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            ulong ul => ul,
            uint u => u,
            int i and >= 0 => (ulong)i,
            long l and >= 0 => (ulong)l,
            string s when ulong.TryParse(s, out var p) => p,
            _ => null
        };
    }
}

/// <summary>Frontière WMI. Toute exception System.Management est convertie en Result.Fail.</summary>
public interface IWmiQuery
{
    Result<IReadOnlyList<WmiRow>> Query(string wql);
    Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql);
}
