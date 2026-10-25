using DrDxt.Core;
using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// Sources contre sorties : un seul écran, étendus, ou dupliqués ; une duplication entre deux fréquences est dite.
public sealed class EcransDupliquesTests
{
    private static CheminsDAffichage.Chemin C(uint source, uint cible, double? hz, string? nom = null)
        => new(1, source, 1, cible, 10, hz, nom);

    [Fact]
    public void Un_ecran_ou_des_ecrans_etendus_n_ont_rien_a_redire()
    {
        EcransDupliques.Juger([C(0, 1, 200)]).Statut.Should().Be(RepairStatus.ExpectedDefault);
        EcransDupliques.Juger([C(0, 1, 200), C(1, 2, 144)]).Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    // Aucun chemin actif (session à distance, écran éteint) n'est pas « un seul écran ».
    [Fact]
    public void Aucun_chemin_actif_se_dit_non_lu()
        => EcransDupliques.Juger([]).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Une_duplication_entre_deux_frequences_dit_les_deux_sans_chiffrer_un_cout_non_mesure()
    {
        var c = EcransDupliques.Juger([C(0, 1, 240, "Rapide"), C(0, 2, 60, "Lent")]);
        // Observé, pas suspect : tant qu'aucune source ne chiffre le coût, DrDXT dit les deux fréquences sans en faire un défaut.
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Contain("240").And.Contain("60");
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("Rapide").And.Contain("Lent").And.Contain("240").And.Contain("60");
        c.Explication.Should().NotMatchRegex(@"\d\s*ms", "aucune source ni relevé ne chiffre ce que coûte la duplication");
    }

    // 59,94 et 60 Hz sont la même fréquence d'écran (NTSC) : pas deux fréquences différentes.
    [Fact]
    public void Cinquante_neuf_94_et_60_hz_sont_la_meme_frequence()
    {
        var c = EcransDupliques.Juger([C(0, 1, 60), C(0, 2, 60000 / 1001.0)]);
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Be(Docteur.T("TOPO.DupliqueeMeme"), "la même fréquence, pas deux fréquences différentes");
        c.Action.Should().Be(RepairAction.Aucune);
    }

    [Fact]
    public void Non_lu_reste_inconnu()
        => EcransDupliques.Juger(null).Statut.Should().Be(RepairStatus.Unknown);

    // DISPLAYCONFIG_PATH_INFO : 72 octets, source à 0 et 8, cible à 20 et 28, sortie à 36, fréquence à 48 et 52.
    [Fact]
    public void Les_chemins_se_decoupent_aux_decalages_du_sdk()
    {
        var b = new byte[2 * 72];
        BitConverter.GetBytes(7L).CopyTo(b, 0);
        BitConverter.GetBytes(3u).CopyTo(b, 8);
        BitConverter.GetBytes(7L).CopyTo(b, 20);
        BitConverter.GetBytes(42u).CopyTo(b, 28);
        BitConverter.GetBytes(10u).CopyTo(b, 36);
        BitConverter.GetBytes(60000u).CopyTo(b, 48);
        BitConverter.GetBytes(1001u).CopyTo(b, 52);
        var l = CheminsDAffichage.Decouper(b, 2);
        l.Should().HaveCount(2);
        l[0].Should().Be(new CheminsDAffichage.Chemin(7, 3, 7, 42, 10, 60000 / 1001.0, null));
        l[1].Hz.Should().BeNull("fraction 0/0 : fréquence non donnée, jamais 0 Hz");
    }

    // Deux écrans dupliqués dont aucune fréquence n'est lue : « même fréquence » serait une égalité affirmée sans l'avoir lue.
    [Fact]
    public void Des_ecrans_dupliques_sans_frequence_lue_ne_se_disent_pas_a_la_meme_frequence()
    {
        var c = EcransDupliques.Juger([C(0, 1, null), C(0, 1, null)]);

        c.Explication.Should().Be(Docteur.T("TOPO.DupliqueeNonLue"));
        c.Statut.Should().Be(RepairStatus.Unknown);
    }
}
