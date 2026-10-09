using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 04/10/2026 (plan 3b.3) : « prêt à jouer » ne dit une gêne que sur une mesure ; un trou n'est ni une gêne ni un feu vert.
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

    // Revue du 04/10 (P1) : un ping internet isolé perdu, ou tous filtrés alors que la box répond, n'est pas une gêne.
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
}
