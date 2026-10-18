using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les écrans bleus se comptent par trois sources bornées à la même fenêtre ; une seule qui en voit un suffit.
public sealed class EcransBleusTests
{
    private static readonly DateTimeOffset Hier = DateTimeOffset.Now.AddDays(-1);

    [Fact]
    public void Un_vidage_recent_interdit_de_dire_aucun_ecran_bleu()
    {
        // Ni code d'arrêt, ni rapport BugCheck, mais un petit vidage écrit hier : ce n'est jamais « aucun ».
        var c = EcransBleus.Juger([], [], new EtatDesVidages([], 1, false, DernierVidage: Hier), 90);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("Au moins 1").And.Contain("ne donnent pas le même nombre");
    }

    [Fact]
    public void Trois_sources_a_zero_disent_aucun()
        => EcransBleus.Juger([], [], new EtatDesVidages([], 0, false), 90).Statut.Should().Be(RepairStatus.ExpectedDefault);

    // Une source non lue n'est pas une source à zéro.
    [Fact]
    public void Une_source_non_lue_empeche_de_conclure_aucun()
    {
        var c = EcransBleus.Juger([], null, new EtatDesVidages([], 0, false), 90);

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.ValeurCourante.Should().Contain("non lu");
    }

    [Fact]
    public void Le_code_d_arret_seul_compte_et_dit_qu_aucun_vidage_n_a_ete_garde()
    {
        var c = EcransBleus.Juger([new ArretBrutal(Hier, 0x139, false, false), new ArretBrutal(Hier, 0, false, true)], [Hier],
            new EtatDesVidages([], 0, false), 90);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("code d'arrêt : 1, rapports : 1, vidages : 0");
        c.Explication.Should().Contain("Aucun fichier de vidage");
    }

    [Fact]
    public void La_requete_des_rapports_est_bornee_a_la_fenetre_par_le_journal_lui_meme()
    {
        // Sans borne, chaque scan parcourait tout l'historique des rapports BugCheck pour n'en garder que la fenêtre.
        var depuis = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(2));

        EcransBleus.RequeteWer(depuis).Should().Contain("EventID=1001")
            .And.Contain("TimeCreated[@SystemTime>='2026-09-01T10:00:00.0000000Z']");
    }
}
