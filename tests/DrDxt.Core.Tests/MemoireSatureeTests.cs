using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>Une mémoire pleine se dit avec ses gestes, une mémoire large se dit avec sa marge, une
/// mémoire non lue se dit non lue. Lecture seule : aucun geste automatique.</summary>
public sealed class MemoireSatureeTests
{
    private const ulong Go = 1UL << 30;

    // Entre 50 et 500 lectures dures par seconde avec de la place : « pas de pression visible » suivi de « Windows pagine »
    // se contredisait. C'est dit comme vu, sans alarme.
    [Fact]
    public void De_la_place_et_une_pagination_moderee_se_disent_sans_dire_rien_ni_alarmer()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(32 * Go, 20 * Go, 12 * Go, 40 * Go), 120);

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Explication.Should().Contain("pas pleine").And.Contain("Windows pagine").And.NotContain("pas de pression visible");
    }

    [Fact]
    public void Pleine_sous_dix_pour_cent_libres_se_dit_avec_ses_gestes()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(8 * Go, 400 * (1UL << 20), 9 * Go, 12 * Go));

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
        // La virgule suit la langue du docteur : « 0,4 » en français, « 0.4 » en anglais.
        f.ValeurCourante.Should().StartWith("0,4 Go libres sur 8 (4 %)");
        using (Docteur.Portee(new System.Globalization.CultureInfo("en-US")))
            MemoireSaturee.Juger(new MemoireLue(8 * Go, 400 * (1UL << 20), 9 * Go, 12 * Go)).ValeurCourante.Should().StartWith("0.4 ");
        f.Explication.Should().Contain("onglets").And.Contain("démarrage").And.Contain("fichier d'échange").And.Contain("barrette")
            .And.Contain("ne ferme rien", "lecture seule, dite comme telle");
    }

    [Fact]
    public void Engagement_au_dessus_de_la_limite_se_dit_meme_avec_de_la_memoire_libre()
    {
        var f = MemoireSaturee.Juger(new MemoireLue(16 * Go, 4 * Go, 95 * Go / 10, 10 * Go));

        f.Statut.Should().Be(RepairStatus.Suspicious, "95 % de la limite d'engagement : le prochain programme peut manquer de mémoire");
        f.ValeurCourante.Should().Contain("engagement 95 %");
        // L'engagement est de la réservation, pas de la pagination ; le texte ne confond plus les deux.
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

    // Pleine mais calme n'est pas pleine et qui pagine : la mesure sur deux secondes tranche, et rien ne conseille de vider.
    [Fact]
    public void Une_memoire_pleine_mais_calme_n_est_pas_une_alarme()
    {
        var pleine = new MemoireLue(8 * Go, 400 * (1UL << 20), 9 * Go, 12 * Go);

        MemoireSaturee.Juger(pleine, 3).Statut.Should().Be(RepairStatus.Observed);
        MemoireSaturee.Juger(pleine, 3).Explication.Should().Contain("rien n'attend le disque");
        MemoireSaturee.Juger(pleine, 900).Statut.Should().Be(RepairStatus.Suspicious);
        MemoireSaturee.Juger(pleine).Statut.Should().Be(RepairStatus.Suspicious, "non mesuré ne vaut pas calme");
        MemoireSaturee.Juger(new MemoireLue(32 * Go, 20 * Go, 12 * Go, 40 * Go), 900).Statut.Should().Be(RepairStatus.NonDefault,
            "de la place libre : une pagination mesurée pendant le scan, qui lit lui-même le disque, se regarde sans accuser");
        MemoireSaturee.Juger(new MemoireLue(32 * Go, 20 * Go, 12 * Go, 40 * Go), 900).ValeurCourante.Should().EndWith("900 lectures de pages dures/s");
    }
}
