using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 06/10/2026 (chantier B8c) : sur un compte sans droits administrateur, un refus d'accès se disait avec une fausse
// cause (« aucune zone ACPI ne répond ») ou un message d'exception anglais. La cause dite est le manque de droits.
public sealed class SansDroitsTests
{
    private sealed class DemarrageRefuse : IBootPerformanceLog
    {
        public Result<IReadOnlyList<MesureDemarrage>> Lire()
            => Result<IReadOnlyList<MesureDemarrage>>.Fail(ErrorCodes.DetectionFailed, "Attempted to perform an unauthorized operation.");

        public Result<IReadOnlyList<TempsBios>> LireTempsBios() => Result<IReadOnlyList<TempsBios>>.Ok([]);
    }

    private static IReadOnlyList<RepairFinding> Lire(bool eleve)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(),
                new WmiMuet(), demarrage: new DemarrageRefuse(), temperatureHwinfo: () => null, eleve: () => eleve)
            .Lire();

    [Fact]
    public void Sans_droits_le_journal_des_demarrages_dit_la_vraie_cause_en_francais()
        => Lire(eleve: false).Single(c => c.Nom == "mesure de Windows").Explication
            .Should().Be(RepairAuditService.SansDroits);

    // Revue (F-32) : pas de promesse « pour la voir » ; la thermique dit ce qui n'a pas été lu, et qu'il retentera.
    [Fact]
    public void Sans_droits_la_temperature_ne_blame_pas_l_ACPI()
        => Lire(eleve: false).Single(c => c.Nom == "temperature du processeur").Explication
            .Should().StartWith("DrDXT tourne sans droits administrateur").And.Contain("retentera").And.NotContain("ne répond")
            .And.NotContain("pour la voir");

    private sealed class NoyauRefuse : ILiveKernelReportLog
    {
        public Result<IReadOnlyList<LiveKernelReport>> Lire()
            => Result<IReadOnlyList<LiveKernelReport>>.Fail(ErrorCodes.DetectionFailed, "2 rapport(s) … (accès refusé).");
    }

    // F-36 : la cause d'un refus dépend des droits : sans eux, les relancer ; avec, un dossier verrouillé.
    [Theory]
    [InlineData(false, "lance-le en administrateur")]
    [InlineData(true, "dossier des rapports est verrouillé")]
    public void Un_refus_des_rapports_noyau_dit_la_cause_selon_les_droits(bool eleve, string cause)
        => new RepairAuditService(new ServicesMuets(), new NoyauRefuse(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(),
                new WmiMuet(), demarrage: new DemarrageRefuse(), temperatureHwinfo: () => null, eleve: () => eleve)
            .Lire().Single(c => c.Nom == "incident noyau conserve").Explication.Should().Contain(cause);

    [Fact]
    public void Avec_les_droits_le_message_d_origine_reste()
        => Lire(eleve: true).Single(c => c.Nom == "mesure de Windows").Explication
            .Should().Contain("n'a pas pu être lu");
}
