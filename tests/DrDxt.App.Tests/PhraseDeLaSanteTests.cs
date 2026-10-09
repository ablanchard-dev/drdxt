using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Critique UI du 08/10 (25/40) : « 13 points à corriger » en haut, « 12 à corriger » en bas. Le titre compte tout,
/// la phrase du bas l'audit de santé seul : elle le dit. La RAM ne renvoie plus « sous le titre « Mémoire » ».</summary>
public sealed class PhraseDeLaSanteTests
{
    [Fact]
    public void La_phrase_du_bas_dit_qu_elle_ne_compte_que_la_sante()
        => ScanViewModel.PhraseDeLaSante(RepairTriage.Trier([new RepairFinding("D", "n", "v", null, RepairStatus.KnownHarmful, "x")]))
            .Should().StartWith("Dans l'audit de santé seul : 1 à corriger");

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Les_textes_de_la_RAM_ne_decrivent_pas_l_ecran(string culture)
    {
        var l = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        foreach (var cle in new[] { "Finding.Memory.XmpOnWithUncleanShutdowns", "Finding.Memory.SousLaVitesseDuKit", "Finding.Memory.XmpOnSousLeKitAvecArrets" })
            l.Get(cle, new System.Globalization.CultureInfo(culture)).Should().NotContain("sous le titre").And.NotContain("under the");
    }
}
