using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le portail CPUID : la règle d'extension d'AMD, la table sourcée, et « non reconnue » qui ferme tout.
public sealed class GenerationAmdTests
{
    // Signatures CPUID 1 EAX : B40F40 (1Ah modèle 44h, annoncé avec la publication 57896), A60F12 (19h modèle 61h).
    [Theory]
    [InlineData(0x00B40F40u, 0x1A, 0x44)]
    [InlineData(0x00A60F12u, 0x19, 0x61)]
    [InlineData(0x00A20F10u, 0x19, 0x21)]
    [InlineData(0x00870F10u, 0x17, 0x71)]
    public void La_famille_et_le_modele_etendus_s_ajoutent_quand_la_famille_de_base_vaut_F(uint eax, int famille, int modele)
        => GenerationAmd.Decoder(eax).Should().Be((famille, modele));

    // Notre règle : la famille étendue ne compte que si la famille de base vaut 0xF. L'ajouter toujours ferait d'une
    // famille 6 à champ étendu non nul une autre famille.
    [Fact]
    public void Une_famille_de_base_autre_que_F_ignore_les_champs_etendus()
        => GenerationAmd.Decoder(0x01B706A1u).Should().Be((6, 0xA));

    [Fact]
    public void Un_couple_de_la_table_donne_sa_generation_et_son_document()
    {
        var c = GenerationAmd.Juger("AuthenticAMD", 0x00A60F12u).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("Zen 4 (19h / 61h)");
        c.Explication.Should().Contain("56713");
    }

    // Un modèle voisin d'une famille connue est une autre puce : jamais la génération d'à côté.
    [Fact]
    public void Un_modele_absent_de_la_table_reste_non_reconnu_et_ferme_les_lectures_AMD()
    {
        GenerationAmd.Reconnaitre("AuthenticAMD", 0x00A50F00u).Should().BeNull();   // 19h modèle 50h
        GenerationAmd.Juger("AuthenticAMD", 0x00A50F00u).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Chaque_ligne_de_la_table_porte_son_document_AMD()
        => GenerationAmd.Table.Should().OnlyContain(l => l.Document.StartsWith("AMD ") && l.ModeleMin <= l.ModeleMax);

    [Fact]
    public void Un_processeur_Intel_n_ouvre_rien_et_ne_dit_rien()
    {
        GenerationAmd.Reconnaitre("GenuineIntel", 0x00B40F40u).Should().BeNull();
        GenerationAmd.Juger("GenuineIntel", 0x000B0671u).Should().BeEmpty();
    }

    [Fact]
    public void CPUID_non_lu_est_dit_non_lu()
        => GenerationAmd.Juger(null, null).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
}
