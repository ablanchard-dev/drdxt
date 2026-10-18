using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Ce qui ralentit le démarrage (mesuré par Windows) et la place libre du disque système.
public sealed class DemarrageEtDisqueTests
{
    [Fact]
    public void Les_ralentisseurs_se_disent_du_pire_au_moins_pire()
    {
        var t = DateTimeOffset.Now;
        var c = DemarrageEtDisque.JugerDemarrage([
            new(t.AddDays(-2), "Discord", 4200), new(t.AddDays(-1), "Discord", 3100), new(t.AddDays(-3), "Norton", 180)])
            .Should().ContainSingle().Subject;
        c.ValeurCourante.Should().Be("2 programme(s)");
        c.Explication.Should().Contain("Discord (jusqu'à").And.Contain("2 fois");
        c.Explication.IndexOf("Discord", StringComparison.Ordinal).Should().BeLessThan(c.Explication.IndexOf("Norton", StringComparison.Ordinal));
    }

    // Un programme que la mesure du dernier démarrage nomme déjà n'est pas renommé par le récapitulatif de 90 jours :
    // le même retard ne s'annonce qu'une fois.
    [Fact]
    public void Un_programme_deja_nomme_au_dernier_demarrage_ne_l_est_pas_une_seconde_fois()
    {
        var t = DateTimeOffset.Now;
        var lus = new List<DemarrageEtDisque.Ralentisseur> { new(t.AddDays(-1), "Lecteur A", 3100), new(t.AddDays(-20), "Lecteur B", 11100) };

        var c = DemarrageEtDisque.JugerDemarrage(lus, dejaNommes: new HashSet<string>(["lecteur a"], StringComparer.OrdinalIgnoreCase))
            .Should().ContainSingle().Subject;
        c.Explication.Should().Contain("Lecteur B").And.NotContain("Lecteur A");
        c.ValeurCourante.Should().Be("1 programme(s)");
        DemarrageEtDisque.JugerDemarrage(lus, dejaNommes: new HashSet<string>(["Lecteur A", "Lecteur B"])).Should().BeEmpty();
    }

    [Fact]
    public void La_mesure_du_dernier_demarrage_dit_quels_programmes_elle_nomme()
    {
        var jour = DateTimeOffset.Now.AddDays(-1);
        IReadOnlyList<DrDxt.Detection.Hardware.MesureDemarrage> mesures =
        [
            new(jour, true, "", 27_600, 0),
            new(jour.AddMinutes(1), false, "Lecteur A", 0, 3100),
            new(jour.AddDays(-10), false, "Lecteur C", 0, 9000),
        ];

        DrDxt.Detection.Hardware.DemarrageMesure.Nommes(mesures, DateTimeOffset.Now, TimeSpan.FromDays(30)).Should().BeEquivalentTo(["Lecteur A"]);
        DrDxt.Detection.Hardware.DemarrageMesure.Nommes(mesures, DateTimeOffset.Now.AddDays(60), TimeSpan.FromDays(30))
            .Should().BeEmpty("une mesure périmée ne nomme personne");
    }

    [Fact]
    public void Sans_ralentisseur_recent_rien_n_est_dit()
        => DemarrageEtDisque.JugerDemarrage([]).Should().BeEmpty();

    [Fact]
    public void La_place_libre_se_dit_et_ne_se_signale_que_sous_cinq_pour_cent()
    {
        // Machine de référence, 04/10 : 301 Go libres sur 1 999 Go.
        DemarrageEtDisque.JugerDisqueSysteme("C:", 301_224_542_208, 1_999_297_835_008).Single().Statut.Should().Be(RepairStatus.Observed, "une mesure de disque s'observe, elle n'est jamais « conforme »");
        DemarrageEtDisque.JugerDisqueSysteme("C:", 40_000_000_000, 1_000_000_000_000).Single().Statut.Should().Be(RepairStatus.Suspicious);
        DemarrageEtDisque.JugerDisqueSysteme("C:", 1, 0).Should().BeEmpty();
    }

    // Sur la machine de référence, le cache de shaders NVIDIA occupait 98,6 Go (832 fichiers, dont trois
    // de 16 Go). Au-dessus de 10 Go la place est dite, avec le prix d'un vidage ; en dessous, rien.
    [Fact]
    public void Un_gros_cache_de_shaders_se_dit_avec_le_prix_d_un_vidage()
    {
        var c = DemarrageEtDisque.JugerCacheDeShaders(105_869_000_000).Single();
        c.Statut.Should().Be(RepairStatus.Observed, "un cache plein n'est pas une panne");
        c.ValeurCourante.Should().Be("98,6 Go");
        c.Explication.Should().Contain("recompile");
        DemarrageEtDisque.JugerCacheDeShaders(2_000_000_000).Should().BeEmpty();
    }
}
