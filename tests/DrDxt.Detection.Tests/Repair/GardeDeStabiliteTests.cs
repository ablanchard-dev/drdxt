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
}
