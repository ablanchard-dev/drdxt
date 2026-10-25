using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les erreurs matérielles WHEA, jugées sur la gravité de l'en-tête CPER.
public sealed class ErreursMateriellesTests
{
    // Début de l'enregistrement relevé sur la machine de référence le 17/09 : « CPER », révision 0101, FFFFFFFF, 1 section,
    // gravité 3 (information).
    private static readonly byte[] Releve = Convert.FromHexString("435045520101FFFFFFFF0100030000000100");

    [Fact]
    public void La_gravite_se_lit_dans_l_en_tete_CPER()
    {
        ErreursMaterielles.Gravite(Releve).Should().Be(3);
        ErreursMaterielles.Gravite([1, 2, 3]).Should().BeNull();
        ErreursMaterielles.Gravite(Convert.FromHexString("585858580101FFFFFFFF0100020000000000")).Should().BeNull("pas de signature CPER");
    }

    [Fact]
    public void Une_information_n_est_pas_une_erreur()
    {
        var c = ErreursMaterielles.Juger([new(DateTimeOffset.Now.AddDays(-17), 3)]).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.Explication.Should().Contain("aucune erreur").And.Contain("1 enregistrement");
    }

    // Un journal illisible n'est pas « aucune erreur ».
    [Fact]
    public void Un_journal_non_lu_se_dit_non_lu()
        => ErreursMaterielles.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Une_erreur_corrigee_se_dit_et_une_grave_pese_plus()
    {
        var t = DateTimeOffset.Now;
        ErreursMaterielles.Juger([new(t, 2)]).Single().ValeurCourante.Should().Be("1 corrigée(s)");
        var grave = ErreursMaterielles.Juger([new(t, 2), new(t.AddHours(-1), 1)]).Single();
        grave.Statut.Should().Be(RepairStatus.Suspicious);
        grave.ValeurCourante.Should().Be("1 grave(s), 1 corrigée(s)");
    }

    // Une seule erreur corrigée, ou des erreurs corrigées toutes PCIe (un « Advisory Non-Fatal » que le lien relève en gérant son
    // énergie) : à regarder, rien ne prouve un tort. Plusieurs corrigées du processeur, ou une grave : à corriger.
    [Fact]
    public void Une_erreur_corrigee_isolee_ou_des_PCIe_corrigees_sont_a_regarder()
    {
        var t = DateTimeOffset.Now;
        var pcie = new ErreursMaterielles.Origine(null, null, null, 1, 0, 0, 0x10DE, 0x2882, 4, 0x2000);
        ErreursMaterielles.Juger([new(t, 2)]).Single().Statut.Should().Be(RepairStatus.NonDefault);
        ErreursMaterielles.Juger([new(t, 2, Source: pcie), new(t.AddHours(-1), 2, Source: pcie), new(t.AddHours(-2), 2, Source: pcie)])
            .Single().Statut.Should().Be(RepairStatus.NonDefault);
        ErreursMaterielles.Juger([new(t, 2, DuProcesseur: true), new(t.AddHours(-1), 2, DuProcesseur: true)])
            .Single().Statut.Should().Be(RepairStatus.Suspicious, "témoin : plusieurs corrigées du processeur, une marge manque");
    }

    // Toutes PCIe, mais un flot sur le dernier jour : plus une gestion d'énergie ponctuelle, à corriger, et dit comme un flot
    // du lien. Le même nombre étalé sur des jours reste à regarder.
    [Fact]
    public void Un_flot_d_erreurs_PCIe_corrigees_sur_un_jour_est_a_corriger()
    {
        var t = DateTimeOffset.Now;
        var pcie = new ErreursMaterielles.Origine(null, null, null, 1, 0, 0, 0x10DE, 0x2882, 4, 0x2000);
        var n = ErreursMaterielles.PcieParJourARegarder + 1;

        var flot = ErreursMaterielles.Juger(Enumerable.Range(0, n).Select(i => new ErreursMaterielles.Enregistrement(t.AddHours(-i), 2, Source: pcie)).ToList()).Single();
        flot.Statut.Should().Be(RepairStatus.Suspicious);
        flot.Explication.Should().Contain("flot").And.NotContain("Isolée");

        ErreursMaterielles.Juger(Enumerable.Range(0, n).Select(i => new ErreursMaterielles.Enregistrement(t.AddDays(-i), 2, Source: pcie)).ToList())
            .Single().Statut.Should().Be(RepairStatus.NonDefault, "témoin : étalées sur des jours");
    }

    // Seules les erreurs du processeur jugent un palier ; une erreur PCIe corrigée qu'une
    // carte relève en continu ne doit pas faire croire à une instabilité. UEFI, annexe N : nombre de sections en octet 10,
    // descripteurs à l'octet 128 (72 octets), type de section (GUID) à l'octet 16 du descripteur.
    [Fact]
    public void Une_section_du_processeur_se_distingue_d_une_erreur_PCIe()
    {
        static byte[] Cper(Guid type)
        {
            var b = new byte[128 + 72];
            "CPER"u8.CopyTo(b);
            BitConverter.GetBytes((ushort)1).CopyTo(b, 10);
            BitConverter.GetBytes(2u).CopyTo(b, 12); // corrigée
            type.ToByteArray().CopyTo(b, 128 + 16);
            return b;
        }
        ErreursMaterielles.DuProcesseur(Cper(new Guid("DC3EA0B0-A144-4797-B95B-53FA242B6E1D"))).Should().BeTrue("IA32/X64");
        ErreursMaterielles.DuProcesseur(Cper(new Guid("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB"))).Should().BeTrue("Processor Generic");
        ErreursMaterielles.DuProcesseur(Cper(new Guid("D995E954-BBC1-430F-AD91-B44DCB3C6F35"))).Should().BeFalse("PCI Express");
        ErreursMaterielles.DuProcesseur(null).Should().BeFalse();
    }
}
