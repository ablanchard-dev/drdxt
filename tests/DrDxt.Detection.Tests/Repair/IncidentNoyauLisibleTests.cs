using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Le joueur lisait « 3x 0x193 VIDEO_DXGKRNL_LIVEDUMP (parametre 1 : 0x80e) » sans savoir
/// que cela concerne sa carte graphique. La valeur reste telle quelle (le journal la compare) ;
/// l'explication dit d'ou vient l'incident, et avoue ne pas connaitre le sens du parametre.
/// </summary>
public class IncidentNoyauLisibleTests
{
    private sealed class RapportsFictifs(params LiveKernelReport[] rapports) : ILiveKernelReportLog
    {
        public Result<IReadOnlyList<LiveKernelReport>> Lire() => Result<IReadOnlyList<LiveKernelReport>>.Ok(rapports);
    }

    private static RepairFinding Constat(params LiveKernelReport[] rapports)
        => new RepairAuditService(new ServicesMuets(), new RapportsFictifs(rapports), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
            .Lire()
            .Single(c => c.Nom == "incident noyau conserve");

    private static LiveKernelReport Rapport(uint code, string? nom, int jour)
        => new(new DateTimeOffset(2026, 8, jour, 12, 0, 0, TimeSpan.Zero), code, nom, "WATCHDOG", "80e");

    [Fact]
    public void Un_incident_graphique_dit_qu_il_vient_de_la_carte_graphique()
    {
        var c = Constat(Rapport(0x193, "VIDEO_DXGKRNL_LIVEDUMP", 15), Rapport(0x193, "VIDEO_DXGKRNL_LIVEDUMP", 18),
            Rapport(0x193, "VIDEO_DXGKRNL_LIVEDUMP", 22));

        c.ValeurCourante.Should().Contain("0x193 VIDEO_DXGKRNL_LIVEDUMP", "la valeur est une cle du journal");
        c.Explication.Should().Contain("carte graphique");
        c.Explication.Should().Contain("ne sait pas");
        // Le titre est commun à tous les incidents ; la première phrase nomme lequel.
        c.Explication.Should().StartWith("3 fois la même signature, 0x193 VIDEO_DXGKRNL_LIVEDUMP : ");
    }

    private sealed class RapportsIllisibles : ILiveKernelReportLog
    {
        public Result<IReadOnlyList<LiveKernelReport>> Lire()
            => Result<IReadOnlyList<LiveKernelReport>>.Fail(ErrorCodes.TweakSdkUnavailable, "refuse");
    }

    [Fact]
    public void Une_lecture_ratee_garde_le_meme_nom_pour_que_le_journal_ne_croie_pas_a_une_disparition()
    {
        // Sous un autre nom, le journal comparait « incident noyau conserve » d'hier a
        // rien aujourd'hui et ecrivait « Disparu ». Sous le meme nom, c'est un statut devenu Unknown.
        var c = new RepairAuditService(new ServicesMuets(), new RapportsIllisibles(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
            .Lire().Where(x => x.Domaine == "STABILITE" && x.Statut == RepairStatus.Unknown).ToList();

        c.Should().Contain(x => x.Nom == "incident noyau conserve");
        c.Should().NotContain(x => x.Nom == "incidents noyau");
    }

    [Fact]
    public void Un_incident_non_graphique_ne_parle_pas_de_carte_graphique()
    {
        var c = Constat(Rapport(0x144, "BUGCODE_USB3_DRIVER", 22));

        c.Explication.Should().NotContain("carte graphique");
    }

    // La machine de référence affichait « suspect » pour trois vidages à chaud du mois d'août,
    // rien depuis. Répétée ET récente, c'est une condition qui revient ; ancienne, c'est un fait d'archive daté.
    private static LiveKernelReport IlYA(int jours)
        => new(DateTimeOffset.UtcNow.AddDays(-jours), 0x193, "VIDEO_DXGKRNL_LIVEDUMP", "WATCHDOG", "80e");

    [Fact]
    public void Trois_vidages_recents_sont_suspects_trois_anciens_un_fait_date()
    {
        Constat(IlYA(2), IlYA(9), IlYA(20)).Statut.Should().Be(RepairStatus.Suspicious);

        var ancien = Constat(IlYA(48), IlYA(52), IlYA(55));
        ancien.Statut.Should().Be(RepairStatus.Observed);
        ancien.Explication.Should().StartWith("3 fois la même signature, 0x193 VIDEO_DXGKRNL_LIVEDUMP : ")
            .And.Contain("rien depuis le " + DateTimeOffset.UtcNow.AddDays(-48).ToLocalTime().ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Un vidage à chaud isolé (« la machine a continué de tourner ») ouvrait la
    // chaîne des incidents (« un souci est en cours », point de restauration à préférer). Seuls les répétés y entrent.
    [Fact]
    public void Un_vidage_a_chaud_isole_n_ouvre_pas_un_souci_en_cours()
    {
        RepairFinding? PointSain(params LiveKernelReport[] r)
            => new RepairAuditService(new ServicesMuets(), new RapportsFictifs(r), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
                .Lire().SingleOrDefault(c => c.Nom == "point de restauration sain");

        PointSain(IlYA(2)).Should().BeNull();
        PointSain(IlYA(2), IlYA(9), IlYA(20)).Should().NotBeNull();
    }
}
