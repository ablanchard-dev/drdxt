using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les gardes Curve Optimizer et XMP lisent les mêmes incidents : arrêts brutaux, écrans bleus, vidages, erreurs graves,
// hausses d'arrêts dangereux du SSD. Une source non lue rend tout non lu.
public sealed class GardeDeStabiliteTests
{
    [Fact]
    public void Un_vidage_sans_arret_brutal_compte_et_une_source_non_lue_rend_non_lu()
    {
        var vidage = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

        GardeDeStabilite.Dates(([], [vidage])).Should().Equal(vidage);
        GardeDeStabilite.Dates((null, [vidage])).Should().BeNull("non lu vaut instable, jamais stable");
    }

    // Une source qui lève (journal refusé d'une autre façon que celles qu'elle attrape) rend la garde non lue ; elle faisait
    // échouer toute la lecture des réglages, donc le scan entier.
    private sealed class VidagesQuiLevent : IDumpCreationLog
    {
        public Result<EtatDesVidages> Lire(DateTimeOffset depuis) => throw new InvalidOperationException("journal (test)");
    }

    private sealed class AucunArret : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest) => Result<IReadOnlyList<DateTimeOffset>>.Ok([]);
    }

    [Fact]
    public void Une_source_qui_leve_rend_la_garde_non_lue()
    {
        var lu = GardeDeStabilite.Lire(new AucunArret(), new VidagesQuiLevent(), DateTimeOffset.UtcNow, _ => [], _ => []);

        GardeDeStabilite.Dates(lu).Should().BeNull();
    }
}
