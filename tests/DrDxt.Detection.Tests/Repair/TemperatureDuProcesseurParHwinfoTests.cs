using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Quand HWiNFO publie le capteur du paquet, la carte dit la vraie température et sa source ;
// sans lui, la phrase d'avant reste, et dit comment l'obtenir.
public sealed class TemperatureDuProcesseurParHwinfoTests
{
    private static RepairFinding Constat(TemperatureHwinfo.Lecture? lu)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), new WmiMuet(), temperatureHwinfo: () => lu)
            .Lire()
            .Single(c => c.Domaine == "THERMIQUE" && c.Nom == "temperature du processeur");

    [Fact]
    public void HWiNFO_lu_la_temperature_est_dite_avec_sa_source()
    {
        var c = Constat(new TemperatureHwinfo.Lecture(82.4, "CPU Package"));
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("82 °C");
        c.Explication.Should().Contain("HWiNFO").And.Contain("CPU Package");
    }

    // Lue par PawnIO (registres, Intel seulement), 100 °C en partie était dite « observée » et « lue dans
    // HWiNFO ». Les registres se jugent comme le paquet, nomment leur source, et à la limite disent le freinage.
    [Fact]
    public void Lue_par_les_registres_la_limite_est_jugee_et_la_source_est_la_bonne()
    {
        var c = Constat(new TemperatureHwinfo.Lecture(100, TemperaturePawnIo.Libelle));
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("registres du processeur").And.NotContain("HWiNFO")
            .And.Contain("se freine lui-même").And.Contain("refroidissement");

        var tiede = Constat(new TemperatureHwinfo.Lecture(72, TemperaturePawnIo.Libelle));
        tiede.Statut.Should().Be(RepairStatus.Observed);
        tiede.Explication.Should().NotContain("HWiNFO");
    }

    // Un HWiNFO ancien (2012) peut tourner sans rien publier : « pas pu lire » seul ne suffit pas. Quand
    // HWiNFO tourne sans publier, la carte nomme sa version et les deux causes connues.
    [Fact]
    public void HWiNFO_qui_tourne_sans_publier_est_nomme_avec_sa_version_et_le_geste()
    {
        var c = Thermique("4, 1, 0, 0", publie: false);

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("HWiNFO 4, 1, 0, 0 tourne").And.Contain("Shared Memory Support")
            .And.Contain("peut la décocher au bout de 12 heures").And.Contain("mets-le à jour");
    }

    // HWiNFO publie, mais la lecture échoue (en pause, capteur au nom inattendu) : ne pas
    // envoyer cocher une case déjà cochée.
    [Fact]
    public void HWiNFO_qui_publie_sans_temperature_lisible_ne_renvoie_pas_a_la_case()
    {
        var c = Thermique("8.54-6090", publie: true);

        c.Explication.Should().Contain("HWiNFO 8.54-6090 publie").And.NotContain("Shared Memory Support");
    }

    // Version illisible (HWiNFO lancé sous SYSTEM) : pas de « HWiNFO ? ».
    [Fact]
    public void Une_version_illisible_n_est_pas_un_point_d_interrogation()
        => Thermique("?", publie: false).Explication.Should().Contain("HWiNFO tourne").And.NotContain("HWiNFO ?");

    private static RepairFinding Thermique(string version, bool publie)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), new WmiMuet(), temperatureHwinfo: () => null, hwinfoQuiTourne: () => version,
                hwinfoPublie: () => publie)
            .Lire().Single(c => c.Domaine == "THERMIQUE" && c.Nom == "temperature du processeur");

    [Fact]
    public void HWiNFO_absent_garde_la_phrase_d_avant()
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), new WmiMuet(), temperatureHwinfo: () => null, hwinfoQuiTourne: () => null)
            .Lire().Single(c => c.Domaine == "THERMIQUE" && c.Nom == "temperature du processeur")
            .Explication.Should().NotContain("tourne, mais").And.Contain("Si HWiNFO tourne avec sa mémoire partagée activée");

    [Fact]
    public void Pres_de_la_limite_la_carte_le_signale()
        => Constat(new TemperatureHwinfo.Lecture(97, "CPU Package")).Statut.Should().Be(RepairStatus.Suspicious);

    // Un Ryzen 7000 tourne à 95 °C par conception ; le seuil d'Intel ne le juge pas.
    [Fact]
    public void Un_Ryzen_a_95_degres_n_est_pas_signale()
    {
        var c = Constat(new TemperatureHwinfo.Lecture(95, "CPU (Tctl/Tdie)"));
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().NotContain("Intel");
    }

    [Fact]
    public void Sans_HWiNFO_rien_n_est_invente_et_la_voie_est_dite()
    {
        var c = Constat(null);
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.ValeurCourante.Should().BeNull();
        c.Explication.Should().Contain("HWiNFO tourne avec sa mémoire partagée");
    }

    [Fact]
    public void La_temperature_de_la_carte_graphique_reste_lue_dans_les_deux_cas()
    {
        Lire(new TemperatureHwinfo.Lecture(70, "CPU Package")).Should().Contain(c => c.Nom == "temperature du GPU");
        Lire(null).Should().Contain(c => c.Nom == "temperature du GPU");

        static IReadOnlyList<RepairFinding> Lire(TemperatureHwinfo.Lecture? lu)
            => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), new WmiMuet(), temperatureHwinfo: () => lu).Lire().ToList();
    }
}
