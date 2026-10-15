using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class TestMemoireEnSessionTests
{
    [Fact]
    public void Une_memoire_saine_ne_rend_aucune_erreur()
        => TestMemoireEnSession.EcrireVerifier(new ulong[4096], 42).Should().Be(0);

    [Fact]
    public void Un_seul_bit_retourne_entre_l_ecriture_et_la_lecture_est_vu()
        => TestMemoireEnSession.EcrireVerifier(new ulong[4096], 42, b => b[1234] ^= 1UL << 17).Should().Be(2,
            "le bit retourne fausse le motif ET son complement : deux mots faux");

    [Fact]
    public void Un_bit_colle_a_un_est_vu_meme_quand_le_motif_l_avait_deja_a_un()
    {
        // Colle a 1 : invisible sur le motif si ce bit y valait deja 1, visible sur le complement.
        TestMemoireEnSession.EcrireVerifier(new ulong[4096], 7, b => b[99] |= 1UL << 3).Should().Be(1);
    }

    [Fact]
    public void Deux_adresses_qui_se_recouvrent_sont_vues()
    {
        // Une adresse « fantome » qui recopie sa voisine : le motif par adresse les distingue.
        TestMemoireEnSession.EcrireVerifier(new ulong[4096], 3, b => b[501] = b[500]).Should().Be(2);
    }

    [Fact]
    public void Rien_d_alloue_ne_se_dit_jamais_aucune_erreur()
    {
        // Si aucun bloc n'avait pu etre alloue, la boucle tournait a vide et rendait
        // « aucune erreur en N passe(s) sur 0,0 Go » : une memoire non testee presentee comme propre.
        var r = new DrDxt.Measurement.Memory.ResultatTestMemoire(0, 0, 0, TimeSpan.FromMinutes(5), 0);
        r.Phrase.Should().NotContain("aucune erreur").And.Contain("rien n'a pu être testé");
        var constat = DrDxt.Measurement.Memory.TestMemoireEnSession.Constat(r);
        constat.ValeurCourante.Should().Be("non testé");
        constat.Statut.Should().Be(DrDxt.Core.RepairStatus.Unknown);
    }

    [Fact]
    public void Le_resultat_dit_ce_qui_a_ete_teste_et_ses_limites()
    {
        var r = TestMemoireEnSession.Lancer(8L << 20, TimeSpan.Zero, 2, CancellationToken.None);
        r.Passes.Should().Be(1);
        r.Erreurs.Should().Be(0);
        r.OctetsTestes.Should().Be(8L << 20);
        r.Phrase.Should().Contain("ne prouve pas qu'il n'y en a aucun");
        // Mesure du 24/09 : 8 191 Mo s'affichaient « 7 Go » (division entiere).
        (r with { OctetsTestes = 8191L << 20 }).Phrase.Should().MatchRegex("8[,.]0 Go");
    }
}
