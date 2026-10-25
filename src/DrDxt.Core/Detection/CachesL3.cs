using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DrDxt.Core;

/// <summary>
/// Les caches L3 de la machine, chacun avec les processeurs logiques qu'il sert, sur tous les groupes de processeurs.
/// Sur un Ryzen X3D à deux CCD, les deux L3 n'ont pas la même taille : celui qui porte le cache empilé est le plus gros.
/// Le banc mémoire s'en sert pour déborder du cache, le docteur pour le dire. Lecture seule.
/// </summary>
/// <remarks>
/// Microsoft Learn, GetLogicalProcessorInformationEx (RelationCache = 2) et CACHE_RELATIONSHIP : chaque entrée
/// SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX porte Relationship (4 octets) et Size (4), puis Level (1), Associativity (1),
/// LineSize (2), CacheSize (4), Type (4), Reserved (18), GroupCount (2) et les GROUP_AFFINITY (Mask sur 8 octets, Group
/// sur 2, Reserved sur 6). Un GroupCount à 0 porte un seul masque (même union).
/// </remarks>
public static class CachesL3
{
    /// <param name="Processeurs">Les processeurs logiques servis, en (groupe, numéro dans le groupe).</param>
    public sealed record Cache(long Octets, IReadOnlyList<(int Groupe, int Numero)> Processeurs);

    /// <summary>Pur : les L3 d'un tampon rendu par GetLogicalProcessorInformationEx(RelationCache).</summary>
    public static IReadOnlyList<Cache> Decoder(ReadOnlySpan<byte> tampon)
    {
        var caches = new List<Cache>();
        for (var o = 0; o + 48 <= tampon.Length;)
        {
            var e = tampon[o..];
            var pas = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            if (pas < 48 || o + pas > tampon.Length) break;
            if (BinaryPrimitives.ReadInt32LittleEndian(e) == 2 && e[8] == 3)
            {
                var groupes = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(e[38..]));
                var processeurs = new List<(int, int)>();
                for (var g = 0; g < groupes && 40 + (g + 1) * 16 <= pas; g++)
                {
                    var masque = BinaryPrimitives.ReadUInt64LittleEndian(e[(40 + g * 16)..]);
                    var groupe = BinaryPrimitives.ReadUInt16LittleEndian(e[(48 + g * 16)..]);
                    for (var b = 0; b < 64; b++)
                        if ((masque >> b & 1) != 0) processeurs.Add((groupe, b));
                }
                caches.Add(new Cache(BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), processeurs));
            }
            o += pas;
        }
        return caches;
    }

    /// <summary>Le vrai ; null si l'appel échoue.</summary>
    public static IReadOnlyList<Cache>? Lire()
    {
        try
        {
            uint taille = 0;
            GetLogicalProcessorInformationEx(2, IntPtr.Zero, ref taille);
            if (taille == 0) return null;
            var tampon = new byte[taille];
            var h = GCHandle.Alloc(tampon, GCHandleType.Pinned);
            try { return GetLogicalProcessorInformationEx(2, h.AddrOfPinnedObject(), ref taille) ? Decoder(tampon.AsSpan(0, (int)taille)) : null; }
            finally { h.Free(); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : le constat. Un seul L3, ou tous de la même taille : dit sans plus. Des tailles différentes : le
    /// plus gros est nommé avec ses processeurs logiques, c'est le bloc à cache empilé.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Cache>? caches)
    {
        if (caches is null || caches.Count == 0)
        {
            yield return new RepairFinding("CPU", "caches L3", null, null, RepairStatus.Unknown, Docteur.T("L3.NonLu"));
            yield break;
        }
        static string Mo(long o) => (o / (1024.0 * 1024)).ToString("0.#", Docteur.Langue);
        var valeur = string.Join(" + ", caches.Select(c => Docteur.T("U.Mo", Mo(c.Octets))));
        if (caches.Select(c => c.Octets).Distinct().Count() == 1)
        {
            yield return new RepairFinding("CPU", "caches L3", valeur, null, RepairStatus.Observed, Docteur.T("L3.Uniforme", caches.Count, Mo(caches[0].Octets)));
            yield break;
        }
        var gros = caches.MaxBy(c => c.Octets)!;
        yield return new RepairFinding("CPU", "caches L3", valeur, null, RepairStatus.Observed,
            Docteur.T("L3.Asymetrique", Mo(gros.Octets), Plages(gros.Processeurs)));
    }

    /// <summary>Pur : « 0-15 » ou « groupe 1 : 0-7 ».</summary>
    public static string Plages(IReadOnlyList<(int Groupe, int Numero)> processeurs)
        => string.Join(", ", processeurs.GroupBy(p => p.Groupe).Select(g =>
        {
            var n = g.Select(p => p.Numero).Order().ToList();
            var morceaux = new List<string>();
            for (var i = 0; i < n.Count;)
            {
                var j = i;
                while (j + 1 < n.Count && n[j + 1] == n[j] + 1) j++;
                morceaux.Add(i == j ? $"{n[i]}" : $"{n[i]}-{n[j]}");
                i = j + 1;
            }
            return (processeurs.Any(p => p.Groupe != 0) ? Docteur.T("L3.Groupe", g.Key) : "") + string.Join(", ", morceaux);
        }));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int RelationshipType, IntPtr Buffer, ref uint ReturnedLength);
}
