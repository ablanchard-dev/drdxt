namespace DrDxt.Detection.Settings;

/// <summary>
/// Décodage du tampon rendu par <c>GetSystemCpuSetInformation</c> (kernel32, Windows 10+).
///
/// Pourquoi ce fichier existe : le lecteur de cœurs parqués était un bouchon qui rendait
/// <b>toujours</b> une liste vide, avec un TODO. Deux consommateurs en tiraient des conclusions
/// opposées — le barème lisait « aucun parking, machine saine » et laissait deux leviers muets,
/// pendant que l'heuristique X3D lisait « rien n'est parqué, donc l'optimiseur ne route pas » et
/// faisait proposer une <b>écriture</b> sur toute machine X3D bi-CCD, saines comprises.
///
/// Choix du mécanisme : l'API CPU Sets est documentée, en espace utilisateur, sans élévation et
/// <b>sans dépendance nouvelle</b>. Le compteur de performance « Parking Status » existe aussi
/// mais son nom est <b>localisé</b> (« État de parcage » sur un Windows français) : s'y fier
/// obligerait à résoudre les noms par index, et à ajouter un paquet.
///
/// Disposition de <c>SYSTEM_CPU_SET_INFORMATION</c>, vérifiée en direct avant d'être codée ici
/// (28 structures, 18 processeurs logiques parqués, jumeaux SMT parqués par paires) :
/// <code>
/// 0  DWORD Size
/// 4  DWORD Type            (0 = CpuSetInformation)
/// 8  DWORD Id
/// 12 WORD  Group
/// 14 BYTE  LogicalProcessorIndex
/// 15 BYTE  CoreIndex
/// 16 BYTE  LastLevelCacheIndex
/// 17 BYTE  NumaNodeIndex
/// 18 BYTE  EfficiencyClass
/// 19 BYTE  AllFlags        (bit 0 = Parked)
/// </code>
/// Pur et total : aucune I/O, jamais d'exception. Un tampon tronqué ou incohérent produit ce qui
/// a pu être lu, jamais une lecture hors des bornes.
/// </summary>
internal static class CpuSetParsing
{
    private const int OffsetSize = 0;
    private const int OffsetType = 4;
    private const int OffsetCoreIndex = 15;
    private const int OffsetAllFlags = 19;
    private const int MinimumEntrySize = OffsetAllFlags + 1;

    private const int CpuSetInformationType = 0;
    private const byte ParkedFlag = 0x01;

    /// <summary>
    /// Les index des cœurs dont <b>tous</b> les processeurs logiques sont parqués, triés.
    ///
    /// Windows parque des cœurs entiers : un seul jumeau SMT parqué n'est pas un cœur parqué, et
    /// l'annoncer ferait conclure à un symptôme qui n'existe pas. La liste vide est un résultat
    /// — « lu, aucun cœur parqué » — et jamais un échec de lecture, que l'appelant exprime par
    /// <c>null</c>.
    /// </summary>
    /// <param name="buffer">Le tampon natif copié.</param>
    /// <param name="length">Nombre d'octets réellement renseignés par l'API.</param>
    public static IReadOnlyList<int> ParkedCoreIndexes(ReadOnlySpan<byte> buffer, int length)
    {
        var usable = Math.Min(length, buffer.Length);
        if (usable <= 0)
            return Array.Empty<int>();

        // Par cœur : combien de processeurs logiques, et combien sont parqués.
        var seen = new Dictionary<byte, (int Total, int Parked)>();

        var offset = 0;
        while (offset + MinimumEntrySize <= usable)
        {
            var size = BitConverter.ToInt32(buffer[(offset + OffsetSize)..]);

            // Un Size nul ou négatif ferait boucler indéfiniment sur le même offset : on s'arrête
            // au lieu de deviner la taille suivante.
            if (size < MinimumEntrySize || offset + size > usable)
                break;

            if (BitConverter.ToInt32(buffer[(offset + OffsetType)..]) == CpuSetInformationType)
            {
                var core = buffer[offset + OffsetCoreIndex];
                var parked = (buffer[offset + OffsetAllFlags] & ParkedFlag) != 0;

                var (total, parkedCount) = seen.TryGetValue(core, out var current) ? current : (0, 0);
                seen[core] = (total + 1, parkedCount + (parked ? 1 : 0));
            }

            offset += size;
        }

        return seen
            .Where(entry => entry.Value.Total > 0 && entry.Value.Parked == entry.Value.Total)
            .Select(entry => (int)entry.Key)
            .OrderBy(index => index)
            .ToArray();
    }
}
