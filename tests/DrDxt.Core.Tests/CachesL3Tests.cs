using System.Buffers.Binary;
using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les L3 de la machine, sur tous les groupes de processeurs (valeurs fictives : deux CCD de tailles différentes).
public sealed class CachesL3Tests
{
    // Une entrée SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX de cache, avec ses GROUP_AFFINITY.
    private static byte[] Entree(byte niveau, uint octets, ushort nombreDeGroupes, params (ulong Masque, ushort Groupe)[] masques)
    {
        var taille = 40 + Math.Max(1, masques.Length) * 16;
        var e = new byte[taille];
        BinaryPrimitives.WriteInt32LittleEndian(e, 2);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), taille);
        e[8] = niveau;
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), octets);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(38), nombreDeGroupes);
        for (var i = 0; i < masques.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(40 + i * 16), masques[i].Masque);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(48 + i * 16), masques[i].Groupe);
        }
        return e;
    }

    [Fact]
    public void Deux_L3_de_tailles_differentes_nomment_le_bloc_a_cache_empile()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var tampon = Entree(2, 1 << 20, 1, (0xFFFF, 0))
            .Concat(Entree(3, 96u << 20, 1, (0x00FF, 0)))
            .Concat(Entree(3, 32u << 20, 1, (0xFF00, 0))).ToArray();
        var caches = CachesL3.Decoder(tampon);
        caches.Select(c => c.Octets).Should().Equal(96L << 20, 32L << 20);
        CachesL3.Juger(caches).Single().Explication.Should().Contain("96 Mo").And.Contain("0-7");
    }

    // Notre règle : tous les groupes sont lus, pas seulement le premier masque.
    [Fact]
    public void Un_cache_qui_sert_deux_groupes_rend_les_processeurs_des_deux()
    {
        var caches = CachesL3.Decoder(Entree(3, 64u << 20, 2, (0b11, 0), (0b11, 1)));
        caches.Single().Processeurs.Should().Equal((0, 0), (0, 1), (1, 0), (1, 1));
        CachesL3.Plages(caches.Single().Processeurs).Should().Contain("1").And.Contain("0-1");
    }

    [Fact]
    public void Un_nombre_de_groupes_a_zero_porte_un_seul_masque()
        => CachesL3.Decoder(Entree(3, 33u << 20, 0, (0b1111, 0))).Single().Processeurs.Should().HaveCount(4);

    [Fact]
    public void Un_tampon_tronque_ou_vide_ne_rend_rien_et_non_lu_est_dit()
    {
        CachesL3.Decoder(Entree(3, 1, 1, (1, 0)).AsSpan(0, 20)).Should().BeEmpty();
        CachesL3.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Des_L3_identiques_sont_dits_uniformes()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        CachesL3.Juger([new(32L << 20, []), new(32L << 20, [])]).Single().Explication.Should().Contain("2 cache(s) L3 de 32 Mo");
    }
}
