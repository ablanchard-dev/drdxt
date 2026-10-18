using DrDxt.Core;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// Qui occupe la montée : les plus gros envois, par programme, en kbit/s.
public sealed class EnvoisParProgrammeTests
{
    private static readonly Dictionary<int, string> Noms = new() { [1] = "voix", [2] = "synchro", [3] = "voix", [4] = "lanceur", [5] = "petit" };

    [Fact]
    public void Les_plus_gros_envois_par_programme_en_kbits()
    {
        // 3 s : 525 000 octets = 1 400 kbit/s ; les processus d'un même nom s'additionnent.
        var c = EnvoisParProgramme.Juger(new Dictionary<int, long> { [1] = 300_000, [3] = 225_000, [2] = 37_500, [4] = 3_750, [5] = 375 },
            TimeSpan.FromSeconds(3), pid => Noms[pid]);
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().StartWith("voix 1").And.Contain("synchro 100").And.Contain("lanceur 10").And.NotContain("petit");
    }

    // Aucune phrase ne suppose l'état du jeu (ouvert ou fermé), et « pas pu écouter » reste inconnu.
    [Fact]
    public void Rien_d_envoye_et_non_lu_ne_se_confondent_pas()
    {
        EnvoisParProgramme.Juger(new Dictionary<int, long>(), TimeSpan.FromSeconds(3), _ => "x").Statut.Should().Be(RepairStatus.ExpectedDefault);
        EnvoisParProgramme.Juger(null, TimeSpan.FromSeconds(3), _ => "x").Statut.Should().Be(RepairStatus.Unknown);
    }

    // Relevé du 17/10/2026 (wevtutil gp) : les quatre envois, TCP et UDP sur IPv4 et IPv6, portent le PID puis la taille.
    [Fact]
    public void Les_quatre_evenements_d_envoi_et_leur_tete()
    {
        EnvoisUdp.EvenementsEnvoi.Should().Equal(10, 26, 42, 58);
        EnvoisUdp.PidEtTaille([0x39, 0x30, 0, 0, 0xDC, 0x05, 0, 0]).Should().Be((12345, 1500));
    }
}
