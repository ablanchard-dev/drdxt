using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le journal des vidages (journal Système et dossier Minidump) compte ce qu'il trouve dans la fenêtre demandée : un scan
// le lit une fois par fenêtre, 90 jours pour les familles des arrêts, 45 jours pour la garde de stabilité.
public sealed class UnScanLitLesVidagesUneFoisParFenetreTests
{
    private sealed class VidagesQuiComptent : IDumpCreationLog
    {
        public readonly List<DateTimeOffset> Fenetres = [];

        public Result<EtatDesVidages> Lire(DateTimeOffset depuis)
        {
            lock (Fenetres) Fenetres.Add(depuis);
            return Result<EtatDesVidages>.Ok(new EtatDesVidages([], 0, false));
        }
    }

    [Fact]
    public void Un_scan_lit_les_vidages_une_fois_par_fenetre()
    {
        var vidages = new VidagesQuiComptent();
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), vidages, new WmiMuet());

        audit.Lire();

        vidages.Fenetres.Should().HaveCount(2, "une lecture sur 90 jours et une sur 45, pas une par famille");
        audit.Lire();
        vidages.Fenetres.Should().HaveCount(4, "un nouveau scan relit");
    }
}
