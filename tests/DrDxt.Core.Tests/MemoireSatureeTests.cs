using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>BUREAU (09/10/2026) : une mémoire pleine se dit avec ses gestes, une mémoire large se dit avec sa marge, une
/// mémoire non lue se dit non lue. Lecture seule : aucun geste automatique.</summary>
public sealed class MemoireSatureeTests
{
    private const ulong Go = 1UL << 30;

    [Fact]
    public void Pleine_sous_dix_pour_cent_libres_se_dit_avec_ses_gestes()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(8 * Go, 400 * (1UL << 20), 9 * Go, 12 * Go));

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
        f.ValeurCourante.Should().StartWith("0.4 Go libres sur 8 (4 %)");
        f.Explication.Should().Contain("onglets").And.Contain("démarrage").And.Contain("fichier d'échange").And.Contain("barrette")
            .And.Contain("ne ferme rien", "lecture seule, dite comme telle");
    }

    [Fact]
    public void Engagement_au_dessus_de_la_limite_se_dit_meme_avec_de_la_memoire_libre()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(16 * Go, 4 * Go, 95 * Go / 10, 10 * Go));

        f.Statut.Should().Be(RepairStatus.Suspicious, "95 % de la limite d'engagement : le prochain programme peut manquer de mémoire");
        f.ValeurCourante.Should().Contain("engagement 95 %");
        // Revue du 09/10 : l'engagement est de la réservation, pas de la pagination ; le texte ne confond plus les deux.
        f.Explication.Should().Contain("mémoire virtuelle").And.NotContain("part sur le disque");
        f.ValeurAttendue.Should().Contain("seuils DrDXT");
    }

    [Fact]
    public void Large_se_dit_avec_sa_marge_et_sans_geste()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(32 * Go, 20 * Go, 12 * Go, 40 * Go));

        f.Statut.Should().Be(RepairStatus.ExpectedDefault);
        f.Action.Should().Be(RepairAction.Aucune);
        f.ValeurCourante.Should().Be("20 Go libres sur 32 (62 %), engagement 30 % de la limite");
    }

    [Fact]
    public void Non_lue_se_dit_non_lue()
    {
        MemoireSaturee.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
        MemoireSaturee.Juger(new MemoireLue(0, 0, 0, 0)).Statut.Should().Be(RepairStatus.Unknown, "un total nul est une lecture ratée");
    }
}
