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
