using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les rapports d'écran bleu (WER 1001, journal Système) : un scan les lit une fois, sur 90 jours ; la garde de
// stabilité en tire ses 45 jours.
public sealed class UnScanLitLesRapportsEcransBleusUneFoisTests
{
    [Fact]
    public void Un_scan_lit_les_rapports_d_ecran_bleu_une_seule_fois()
    {
        var lectures = 0;
        var maintenant = DateTimeOffset.UtcNow;
        IReadOnlyList<DateTimeOffset>? Rapports(DateTimeOffset depuis)
        {
            Interlocked.Increment(ref lectures);
            return new[] { maintenant.AddDays(-2), maintenant.AddDays(-60) }.Where(d => d >= depuis).ToList();
        }
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(),
            new WmiMuet(), rapportsEcransBleus: Rapports);

        var constats = audit.Lire();

        lectures.Should().Be(1);
        constats.Should().Contain(c => c.Nom == "ecrans bleus" && c.ValeurCourante!.Contains('2'),
            "témoin : les deux rapports de la fenêtre de 90 jours sont comptés");
        audit.Lire();
        lectures.Should().Be(2, "un nouveau scan relit");
    }

    [Fact]
    public void Une_fenetre_courte_se_tire_de_la_lecture_et_une_plus_large_relit()
    {
        var lectures = 0;
        var t = DateTimeOffset.UtcNow;
        var lecture = new LectureDuScan<DateTimeOffset>(depuis =>
        {
            lectures++;
            return new[] { t.AddDays(-2), t.AddDays(-60), t.AddDays(-100) }.Where(d => d >= depuis).ToList();
        }, d => d, t.AddDays(-90));

        lecture.Depuis(t.AddDays(-45))!.Should().Equal(t.AddDays(-2));
        lecture.Depuis(t.AddDays(-90))!.Should().HaveCount(2);
        lectures.Should().Be(1);
        lecture.Depuis(t.AddDays(-120))!.Should().HaveCount(3);
        lectures.Should().Be(2);
    }
}
