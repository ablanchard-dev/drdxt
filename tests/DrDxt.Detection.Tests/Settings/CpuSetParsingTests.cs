using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Le lecteur de coeurs parques etait un bouchon : il rendait toujours une liste vide, avec un
/// TODO. Deux consommateurs en tiraient des conclusions opposees — le bareme en deduisait
/// « aucun parking, machine saine », et l'heuristique X3D en deduisait « rien n'est parque, donc
/// l'optimiseur ne route pas », ce qui faisait proposer une ecriture (AMD3DVCacheSvc + CppcEnable)
/// sur toute machine X3D bi-CCD, saines comprises.
///
/// Ces tests epinglent le decodage du tampon rendu par GetSystemCpuSetInformation. La disposition
/// est celle de SYSTEM_CPU_SET_INFORMATION (kernel32, Windows 10+) : Size, Type, Id, Group,
/// LogicalProcessorIndex, CoreIndex, LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass, AllFlags
/// — le bit 0 de AllFlags etant Parked. Verifie en direct sur une machine reelle avant d'etre code
/// ici : 28 structures, 18 processeurs logiques parques, jumeaux SMT parques par paires.
/// </summary>
public class CpuSetParsingTests
{
    private const int StructSize = 32;

    /// <summary>Une entree SYSTEM_CPU_SET_INFORMATION synthetique.</summary>
    private static byte[] Entry(byte logical, byte core, bool parked, int type = 0, int size = StructSize)
    {
        var b = new byte[size];
        BitConverter.GetBytes(size).CopyTo(b, 0);
        BitConverter.GetBytes(type).CopyTo(b, 4);
        b[14] = logical;
        b[15] = core;
        b[19] = (byte)(parked ? 0x01 : 0x00);
        return b;
    }

    private static byte[] Buffer(params byte[][] entries) => entries.SelectMany(x => x).ToArray();

    [Fact]
    public void A_core_whose_logical_processors_are_all_parked_is_parked()
    {
        // Coeur 0 : ses deux jumeaux SMT sont parques. Coeur 2 : aucun.
        var buf = Buffer(
            Entry(logical: 0, core: 0, parked: true),
            Entry(logical: 1, core: 0, parked: true),
            Entry(logical: 2, core: 2, parked: false),
            Entry(logical: 3, core: 2, parked: false));

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().Equal(0);
    }

    [Fact]
    public void A_core_with_one_thread_still_running_is_not_reported_as_parked()
    {
        // Windows parque des coeurs entiers. Un seul jumeau parque n'est pas un coeur parque,
        // et l'annoncer ferait conclure a un symptome qui n'existe pas.
        var buf = Buffer(
            Entry(logical: 0, core: 0, parked: true),
            Entry(logical: 1, core: 0, parked: false));

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().BeEmpty();
    }

    [Fact]
    public void Nothing_parked_is_an_empty_list_not_a_failure()
    {
        // Distinction centrale : liste vide = lu, aucun coeur parque. C'est un resultat,
        // pas une absence de resultat.
        var buf = Buffer(Entry(0, 0, false), Entry(1, 0, false));

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Cores_are_reported_once_each_and_in_order()
    {
        var buf = Buffer(
            Entry(4, 4, true), Entry(5, 4, true),
            Entry(0, 0, true), Entry(1, 0, true),
            Entry(2, 2, false));

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().Equal(0, 4);
    }

    [Fact]
    public void Entries_of_another_type_are_ignored()
    {
        // Type != CpuSetInformation : l'API peut en rendre d'autres, on ne les interprete pas.
        var buf = Buffer(Entry(0, 0, true, type: 7), Entry(1, 1, true));

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().Equal(1);
    }

    [Fact]
    public void A_zero_or_negative_size_stops_the_walk_instead_of_looping_forever()
    {
        // Un Size a zero ferait tourner la boucle indefiniment sur le meme offset.
        var buf = Buffer(Entry(0, 0, true), Entry(9, 9, true, size: StructSize));
        BitConverter.GetBytes(0).CopyTo(buf, StructSize); // corrompt le Size de la 2e entree

        CpuSetParsing.ParkedCoreIndexes(buf, buf.Length).Should().Equal(0);
    }

    [Fact]
    public void A_truncated_last_entry_is_not_read_past_the_buffer()
    {
        var full = Buffer(Entry(0, 0, true), Entry(1, 1, true));

        // On declare moins d'octets que le tampon n'en contient : la 2e entree est incomplete.
        CpuSetParsing.ParkedCoreIndexes(full, StructSize + 10).Should().Equal(0);
    }
}
