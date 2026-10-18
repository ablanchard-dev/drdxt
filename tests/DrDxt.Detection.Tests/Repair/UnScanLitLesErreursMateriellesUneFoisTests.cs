using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les erreurs matérielles WHEA (journal Système) : un scan les lit une fois, sur 90 jours ; la garde de stabilité en
// tire ses 45 jours.
public sealed class UnScanLitLesErreursMateriellesUneFoisTests
{
    [Fact]
    public void Un_scan_lit_les_erreurs_materielles_une_seule_fois()
    {
        var lectures = 0;
        var maintenant = DateTimeOffset.UtcNow;
        IReadOnlyList<ErreursMaterielles.Enregistrement>? Whea(int jours)
        {
            Interlocked.Increment(ref lectures);
            return new ErreursMaterielles.Enregistrement[] { new(maintenant.AddDays(-3), 1), new(maintenant.AddDays(-70), 1) }
                .Where(e => e.Quand >= maintenant.AddDays(-jours)).ToList();
        }
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(),
            new WmiMuet(), erreursMaterielles: Whea);

        audit.Lire();

        lectures.Should().Be(1);
        audit.Lire();
        lectures.Should().Be(2, "un nouveau scan relit");
    }
}
