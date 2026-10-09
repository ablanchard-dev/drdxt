using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 04/10/2026 (plan 3.2) : un arrêt ou une erreur matérielle dans la semaine d'un changement le désigne ; une semaine
// propre se dit aussi.
public sealed class StabiliteApresChangementTests
{
    private static readonly DateTimeOffset Changement = new(2026, 10, 4, 4, 35, 52, TimeSpan.FromHours(2));
    private static readonly Changement Bios = new(Changement, "le réglage du BIOS « A » passé à « B »", null, null);

    [Fact]
    public void Un_arret_dans_la_semaine_designe_le_changement()
    {
        var c = StabiliteApresChangement.Juger([Bios], [Changement.AddDays(2)], [], Changement.AddDays(3)).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Contain("1 arrêt");
        c.Explication.Should().Contain("« A »").And.Contain("revenir au réglage d'avant");
    }

    [Fact]
    public void Une_erreur_materielle_dans_la_semaine_compte_aussi()
        => StabiliteApresChangement.Juger([Bios], [], [Changement.AddDays(1)], Changement.AddDays(3)).Single()
            .ValeurCourante.Should().Contain("1 erreur");

    [Fact]
    public void Une_semaine_propre_en_cours_se_dit_et_finie_ne_dit_plus_rien()
    {
        var enCours = StabiliteApresChangement.Juger([Bios], [Changement.AddDays(-1)], [], Changement.AddDays(2)).Single();
        enCours.Statut.Should().Be(RepairStatus.Observed, "l'arrêt d'AVANT le changement ne compte pas");
        enCours.Explication.Should().Contain("sur les 7");
        StabiliteApresChangement.Juger([Bios], [], [], Changement.AddDays(10)).Should().BeEmpty("surveillance finie, rien à signaler");
        StabiliteApresChangement.Juger([Bios], [Changement.AddDays(9)], [], Changement.AddDays(10)).Should().BeEmpty("hors de la semaine");
    }

    [Fact]
    public void Une_pose_ou_un_vieux_changement_ne_comptent_pas()
    {
        var pose = new Changement(Changement, "latency.hagsOn", null, "pose:latency.hagsOn");
        StabiliteApresChangement.Juger([pose], [Changement.AddDays(1)], [], Changement.AddDays(2)).Should().BeEmpty();
        StabiliteApresChangement.Juger([Bios], [Changement.AddDays(1)], [], Changement.AddDays(40)).Should().BeEmpty();
    }

    // UI1 (08/10/2026, vu sur capture) : « 1 arrêt(s) brutal(aux) (… 1,5 jour(s) après) ». Les mots s'accordent au nombre.
    [Fact]
    public void Les_mots_s_accordent_au_nombre()
    {
        var un = StabiliteApresChangement.Juger([Bios], [Changement.AddDays(1.5)], [Changement.AddDays(1)], Changement.AddDays(3)).Single();
        un.ValeurCourante.Should().Contain("1 arrêt brutal (").And.Contain("1,5 jour après").And.Contain("1 erreur matérielle").And.NotContain("(s)");

        var deux = StabiliteApresChangement.Juger([Bios], [Changement.AddDays(2), Changement.AddDays(3)], [], Changement.AddDays(4)).Single();
        deux.ValeurCourante.Should().Contain("2 arrêts brutaux").And.Contain("2 jours après");

        StabiliteApresChangement.Juger([Bios], [], [], Changement.AddDays(3)).Single().Explication.Should().Contain("3 jours sur les 7");
    }

    // UI1 (critique du 08/10) : trois constats « Après un changement » accusaient chacun un changement différent du même
    // et unique arrêt. Les changements qui désignent les mêmes incidents se disent ensemble, sans en accuser un.
    [Fact]
    public void Plusieurs_changements_avant_le_meme_arret_font_un_seul_constat()
    {
        var bios = Bios;
        var jeu = new Changement(Changement.AddHours(4), "les réglages graphiques du jeu", null, null);
        var carte = new Changement(Changement.AddHours(4.1), "la carte de l'événement", null, null);
        var arret = Changement.AddDays(1.7);

        var c = StabiliteApresChangement.Juger([bios, jeu, carte], [arret], [], Changement.AddDays(3)).Should().ContainSingle().Subject;

        c.ValeurCourante.Should().Be("1 arrêt brutal (le " + arret.LocalDateTime.ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ")");
        c.Explication.Should().Contain("3 changements").And.Contain("« A »").And.Contain("réglages graphiques").And.Contain("événement")
            .And.Contain("un à la fois");
    }

    // Revue n° 15 : A voit l'arrêt du jour 5, B ceux des jours 5 et 9. Regrouper sur l'ensemble exact laissait deux constats
    // qui accusaient chacun le même arrêt ; ils partagent un incident, ils se disent ensemble.
    [Fact]
    public void Deux_changements_qui_partagent_un_seul_arret_font_un_seul_constat()
    {
        var b = new Changement(Changement.AddDays(3), "le réglage B", null, null);
        var constats = StabiliteApresChangement.Juger([Bios, b], [Changement.AddDays(5), Changement.AddDays(9)], [], Changement.AddDays(12)).ToList();

        constats.Should().ContainSingle().Which.Explication.Should().Contain("2 changements");
        constats.Single().ValeurCourante.Should().StartWith("2 arrêts brutaux");
    }

    [Fact]
    public void La_sortie_suit_l_ordre_des_changements()
    {
        var calme = new Changement(Changement.AddDays(20), "le réglage calme", null, null);
        var constats = StabiliteApresChangement.Juger([Bios, calme], [Changement.AddDays(2)], [], Changement.AddDays(22)).ToList();
        constats.Select(c => c.Statut).Should().Equal(RepairStatus.Suspicious, RepairStatus.Observed);
    }
}
