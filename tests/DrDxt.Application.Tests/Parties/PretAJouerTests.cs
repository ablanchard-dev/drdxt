using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// « prêt à jouer » ne dit une gêne que sur une mesure ; un trou n'est ni une gêne ni un feu vert.
public sealed class PretAJouerTests
{
    private static readonly EtatAvantPartie Propre = new([("explorer", 3)], 1, 0, 12, 0, 60, 18UL << 30);

    [Fact]
    public void Une_machine_propre_n_a_aucune_gene()
        => PretAJouer.Genes(Propre, k => k).Should().BeEmpty();

    [Fact]
    public void Chaque_gene_mesuree_se_dit()
    {
        var e = Propre with
        {
            Voisins = [("TiWorker", 80), ("explorer", 3)], PertesInternetPourcent = 30, PingBoxMs = 25,
            MemoireLibreOctets = 512UL << 20, Celsius = 93,
        };
        PretAJouer.Genes(e, k => k).Should().Equal("PretAJouer.Voisins", "PretAJouer.Pertes", "PretAJouer.Box",
            "PretAJouer.Memoire", "PretAJouer.Chaleur");
        PretAJouer.Genes(e, k => k == "PretAJouer.Voisins" ? "{0}" : k)[0].Should().Contain("TiWorker").And.NotContain("explorer");
    }

    // Un ping internet isolé perdu, ou tous filtrés alors que la box répond, n'est pas une gêne.
    [Theory]
    [InlineData(10.0)]
    [InlineData(100.0)]
    public void Une_perte_isolee_ou_un_ping_filtre_vers_internet_n_est_pas_une_gene(double pertes)
        => PretAJouer.Genes(Propre with { PertesInternetPourcent = pertes }, k => k).Should().BeEmpty();

    [Fact]
    public void Un_aller_retour_sous_la_milliseconde_se_dit_moins_de_un()
        => PretAJouer.Mesure(Propre with { PingBoxMs = 0 }, k => k == "PretAJouer.Mesure" ? "{0}" : k).Should().Be("< 1 ms");

    [Fact]
    public void Ce_qui_n_a_pas_pu_etre_lu_n_est_pas_une_gene()
        => PretAJouer.Genes(new EtatAvantPartie(null, null, null, null, null, null, null), k => k).Should().BeEmpty();

    // Un vrai redémarrage ancien, de la mémoire vidéo déjà tenue, une chaîne audio virtuelle, une conversion du disque :
    // chacun se dit sur une mesure, jamais sur un trou ; dwm ne compte pas.
    [Fact]
    public void Les_controles_d_avant_partie_se_disent_sur_une_mesure()
    {
        var maintenant = new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);
        var e = Propre with
        {
            DernierDemarrage = maintenant.AddDays(-9), MemoireVideo = [("dwm", 900L << 20), ("chrome", 800L << 20), ("discord", 100L << 20)],
            AudioVirtuel = ["VB-Audio Hi-Fi Cable"], ConversionDuDisque = true,
        };
        PretAJouer.Genes(e, k => k, maintenant).Should().Equal("PretAJouer.Redemarrage", "PretAJouer.MemoireVideo", "PretAJouer.AudioVirtuel",
            "PretAJouer.Conversion");
        // L'unité vient des textes de l'App : « Mo » en français, « MB » en anglais, jamais écrite en dur.
        PretAJouer.Genes(e, k => k switch { "PretAJouer.MemoireVideo" => "{0}", "Unite.Mo" => "MB", _ => k }, maintenant)[1]
            .Should().Be("chrome (800 MB)");
        PretAJouer.Genes(Propre with { DernierDemarrage = maintenant.AddDays(-1), AudioVirtuel = [] }, k => k, maintenant).Should().BeEmpty();
    }
}
