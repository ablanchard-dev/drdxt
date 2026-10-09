using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

public class StabiliteLueTests
{
    private sealed class Journal(Result<IReadOnlyList<LiveKernelReport>> r) : ILiveKernelReportLog
    {
        public Result<IReadOnlyList<LiveKernelReport>> Lire() => r;
    }

    private static LiveKernelReport R(uint code, string? p1, int jours) =>
        new(DateTimeOffset.UtcNow.AddDays(-jours), code, "X", null, p1);

    [Fact]
    public void La_signature_la_plus_REPETEE_l_emporte_et_le_non_graphique_ne_compte_pas()
    {
        var i = StabiliteLue.InstabiliteGpu(new Journal(Result<IReadOnlyList<LiveKernelReport>>.Ok(new[]
        {
            R(0x193, "80e", 1), R(0x193, "80e", 5), R(0x193, "80e", 9),
            R(0x141, "1", 2),
            R(0x1A, "1", 1), R(0x1A, "1", 1), R(0x1A, "1", 1), R(0x1A, "1", 1),   // memoire : pas graphique
        })));

        i!.Occurrences.Should().Be(3);
        i.Signature.Should().Contain("0x193");
    }

    [Fact]
    public void Un_journal_illisible_n_est_pas_un_journal_vide()
        => StabiliteLue.InstabiliteGpu(new Journal(Result<IReadOnlyList<LiveKernelReport>>.Fail("X", "refuse")))
            .Should().BeNull();
}
