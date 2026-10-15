using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>Les arrêts ne refusent plus Optimiser ; un réglage suivi d'un arrêt brutal hors veille se rend.</summary>
public sealed class GardeDIncidentTests
{
    private static readonly DateTimeOffset Pose = new(2026, 10, 8, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_arret_hors_veille_dans_la_semaine_apres_la_pose_rend_le_reglage()
        => GardeDIncident.ARendre([("fixit.power_plan", Pose)], [new ArretClasse(Pose.AddDays(2), FamilleDArret.EcranBleu, 0xEF)])
            .Should().ContainSingle(x => x.Id == "fixit.power_plan" && x.Arret.CodeArret == 0xEF);

    [Theory]
    [InlineData(-1, FamilleDArret.SansCode)]  // avant la pose : il n'accuse pas le réglage
    [InlineData(8, FamilleDArret.SansCode)]   // après la semaine de surveillance
    [InlineData(1, FamilleDArret.Veille)]     // une coupure en veille ne dit rien de la charge
    public void Hors_de_la_garde_rien_n_est_rendu(int jours, FamilleDArret famille)
        => GardeDIncident.ARendre([("fixit.power_plan", Pose)], [new ArretClasse(Pose.AddDays(jours), famille)]).Should().BeEmpty();

    // Le pilote graphique qui décroche (vidage à chaud WER) dans la semaine après une pose rend les
    // réglages de la carte et du pilote, et seulement eux ; le refus « instabilité du pilote » du sélecteur tombe.
    [Theory]
    [InlineData("gpu.power_limit", true)]
    [InlineData("latency.hagsOn", true)]
    [InlineData("fixit.power_plan", false)]
    [InlineData("latency.gameModeOn", false)]
    public void Un_vidage_a_chaud_du_pilote_rend_les_reglages_de_la_carte_et_du_pilote_seulement(string id, bool rendu)
    {
        var r = GardeDIncident.ARendre([(id, Pose)], [], [(Pose.AddDays(1), 0x193)]);
        r.Any().Should().Be(rendu);
        if (rendu) r.Single().Should().Match<(string Id, ArretClasse Arret, bool VidageAChaud)>(x => x.VidageAChaud && x.Arret.CodeArret == 0x193);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void Un_vidage_hors_de_la_semaine_ne_rend_rien(int jours)
        => GardeDIncident.ARendre([("gpu.power_limit", Pose)], [], [(Pose.AddDays(jours), 0x193)]).Should().BeEmpty();

    [Fact]
    public void Arrets_non_lus_rien_n_est_rendu()
        => GardeDIncident.ARendre([("fixit.power_plan", Pose)], null).Should().BeEmpty();

    // GARDE1 : un réglage de la seule session (mode Jeu, heures d'activité) ne peut pas causer un arrêt : il reste.
    [Theory]
    [InlineData("latency.gameModeOn", false)]
    [InlineData("fixit.heures_actives", false)]
    [InlineData("fixit.power_plan", true)]
    [InlineData("levier.inconnu", true)]
    public void Seuls_les_reglages_qui_peuvent_causer_un_arret_se_rendent(string id, bool rendu)
        => GardeDIncident.ARendre([(id, Pose)], [new ArretClasse(Pose.AddDays(1), FamilleDArret.SansCode)])
            .Any().Should().Be(rendu);
}
