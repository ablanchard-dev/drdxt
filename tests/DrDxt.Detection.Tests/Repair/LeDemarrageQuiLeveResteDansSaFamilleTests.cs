using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// La mesure du démarrage est lue avant ses deux familles : si sa lecture lève, seules les familles du démarrage le
// disent, le reste de l'audit est rendu.
public sealed class LeDemarrageQuiLeveResteDansSaFamilleTests
{
    private sealed class DemarrageQuiLeve : IBootPerformanceLog
    {
        public Result<IReadOnlyList<MesureDemarrage>> Lire() => throw new InvalidOperationException("journal en panne");
        public Result<IReadOnlyList<TempsBios>> LireTempsBios() => Result<IReadOnlyList<TempsBios>>.Ok([]);
    }

    [Fact]
    public void Une_lecture_du_demarrage_qui_leve_ne_fait_tomber_que_le_demarrage()
    {
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), null, new MemoireMuette(), new VidagesMuets(), new WmiMuet(),
            demarrage: new DemarrageQuiLeve());

        var constats = audit.Lire();

        constats.Should().Contain(c => c.Domaine == "DEMARRAGE" && c.Statut == RepairStatus.Unknown);
        constats.Should().Contain(c => c.Domaine == "STABILITE", "le reste de l'audit est rendu");
    }
}
