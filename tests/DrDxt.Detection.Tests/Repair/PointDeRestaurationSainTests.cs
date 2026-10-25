using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Un point pris pendant un souci le contient. Le docteur propose le plus récent d'avant le début
// du souci, et ne restaure jamais seul.
public sealed class PointDeRestaurationSainTests
{
    private static readonly TimeSpan Paris = TimeSpan.FromHours(2);
    private static DateTimeOffset T(int mois, int jour, int h = 12) => new(2026, mois, jour, h, 0, 0, Paris);
    private static readonly DateTimeOffset Maintenant = T(10, 6);

    private static FaitDate Arret(DateTimeOffset quand) => new(quand, "arrêt brutal sans code");

    [Fact]
    public void Sans_souci_recent_rien_n_est_dit()
        => PointDeRestaurationSain.Juger([(T(9, 1), "Windows Update")], [Arret(T(8, 1))], Maintenant).Should().BeEmpty();

    [Fact]
    public void Le_point_propose_est_le_plus_recent_d_avant_le_debut_de_la_serie()
    {
        // Série : 20/09, 28/09, 05/10 (écarts ≤ 14 jours) ; un incident isolé plus ancien (01/08) n'en fait pas partie.
        var incidents = new[] { Arret(T(8, 1)), Arret(T(9, 20)), Arret(T(9, 28)), Arret(T(10, 5)) };
        var points = new[] { (T(9, 10), "Installation pilote"), (T(9, 18), "Windows Update"), (T(10, 1), "DrDXT : avant optimisation") };

        var c = PointDeRestaurationSain.Juger(points, incidents, Maintenant).Single();

        c.Nom.Should().Be("point de restauration sain");
        c.ValeurCourante.Should().Contain("Windows Update").And.Contain(T(9, 18).ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
        c.Explication.Should().Contain(T(9, 20).ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain("1 point pris depuis").And.Contain("ne restaure jamais");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Sans_point_d_avant_le_souci_un_point_cree_maintenant_est_dit_le_contenir()
    {
        var c = PointDeRestaurationSain.Juger([(T(10, 1), "DrDXT : avant optimisation")], [Arret(T(9, 25)), Arret(T(10, 5))], Maintenant).Single();

        c.ValeurCourante.Should().Be("aucun point d'avant le souci");
        c.Explication.Should().Contain("garderait le souci");
        c.Action.Should().Be(RepairAction.Aucune);
        // Le souci daté voyage avec le constat, l'écran le nomme.
        // Début de la série : 25/09 ; 05/10 est à moins de deux semaines.
        // Le souci tient sur la ligne d'avertissement de l'accueil : le jour suffit, l'explication garde l'heure.
        c.Provenance.Should().Be("arrêt brutal sans code, le " + T(9, 25).ToLocalTime().ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture));
    }

    // La comparaison se fait en instants. Un point à 00:30 à Paris (22:30 UTC la veille) est
    // d'avant un arrêt à 01:00 à Paris ; une comparaison au jour, ou locale contre UTC, se tromperait.
    [Fact]
    public void Le_point_et_l_incident_se_comparent_en_instants_meme_a_cheval_sur_minuit()
    {
        var arret = new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero);
        PointDeRestaurationSain.Juger([(new DateTimeOffset(2026, 10, 5, 22, 30, 0, TimeSpan.Zero), "Avant")], [Arret(arret)], Maintenant)
            .Single().ValeurCourante.Should().Contain("« Avant »");
        PointDeRestaurationSain.Juger([(new DateTimeOffset(2026, 10, 5, 23, 30, 0, TimeSpan.Zero), "Après")], [Arret(arret)], Maintenant)
            .Single().ValeurCourante.Should().Be("aucun point d'avant le souci");
    }

    // Les incidents ne sont lus que sur 90 jours. Une série qui touche cette borne a peut-être commencé avant :
    // « au moins depuis », et le point proposé n'est pas dit sain sans réserve.
    [Fact]
    public void Une_serie_qui_touche_la_borne_de_lecture_est_dite_au_moins_depuis()
    {
        var lusDepuis = T(7, 8);
        var incidents = new[] { Arret(T(7, 10)), Arret(T(7, 20)), Arret(T(8, 1)), Arret(T(8, 14)), Arret(T(8, 25)), Arret(T(9, 5)),
            Arret(T(9, 15)), Arret(T(9, 25)), Arret(T(10, 5)) };

        var c = PointDeRestaurationSain.Juger([(T(7, 1), "Ancien")], incidents, Maintenant, lusDepuis).Single();

        c.Explication.Should().Contain("au moins depuis le " + T(7, 10).ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain("peut-être commencé avant");
        PointDeRestaurationSain.Juger([(T(7, 1), "Ancien")], incidents, Maintenant).Single().Explication
            .Should().NotContain("au moins depuis");
    }

    // Un seul arrêt pendant une mise en veille, cinq jours plus tôt, sans récidive : pas un souci en cours.
    [Fact]
    public void Un_incident_isole_de_plus_de_trois_jours_n_est_plus_un_souci_en_cours()
    {
        PointDeRestaurationSain.Juger([(T(10, 8), "Windows Update")], [Arret(T(10, 5, 20))], T(10, 10)).Should().BeEmpty();
        PointDeRestaurationSain.Juger([(T(10, 8), "Windows Update")], [Arret(T(10, 8, 20))], T(10, 10)).Should().ContainSingle(
            "deux jours plus tôt, il peut encore revenir");
        PointDeRestaurationSain.Juger([(T(10, 8), "Windows Update")], [Arret(T(9, 28)), Arret(T(10, 5, 20))], T(10, 10)).Should()
            .ContainSingle("deux arrêts à moins de deux semaines : une série, toujours en cours");
    }

    [Fact]
    public void Une_liste_illisible_pendant_un_souci_est_inconnue()
        => PointDeRestaurationSain.Juger(null, [Arret(T(10, 5))], Maintenant).Single().Statut.Should().Be(RepairStatus.Unknown);

    // Journaux d'incidents illisibles : le bilan ne doit pas dire « aucun souci en cours ».
    [Fact]
    public void Des_incidents_non_lus_se_disent_non_lus()
    {
        var c = PointDeRestaurationSain.Juger([(T(9, 1), "Windows Update")], null, Maintenant).Single();

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Nom.Should().Be("point de restauration sain");
    }
}
