using DrDxt.Core;
using DrDxt.Detection.Display;

namespace DrDxt.Detection.Tests.Display;

/// <summary>
/// Écrit depuis une vraie machine : la machine de référence tourne en 1920x1080 à 143 Hz avec un
/// écran 200 Hz branché. DrDXT n'avait aucune sonde d'affichage, donc son scan annonçait une
/// machine saine alors que le plus gros gain gratuit restait non signalé.
/// </summary>
public class DisplayRefreshHeuristicTests
{
    [Fact]
    public void Reports_headroom_when_the_panel_offers_more_at_the_same_resolution()
    {
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 60, 120, 143, 165, 200 });

        Assert.Equal(TriState.On, r.HasHeadroom);
        Assert.Equal(143, r.CurrentHz);
        Assert.Equal(200, r.BestHz);
    }

    [Fact]
    public void Reports_no_headroom_when_already_on_the_best_mode()
    {
        var r = DisplayRefreshHeuristic.Evaluate(200, new[] { 60, 120, 200 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
        Assert.Equal(200, r.BestHz);
    }

    [Fact]
    public void A_one_hertz_gap_is_the_same_mode_not_an_opportunity()
    {
        // Windows annonce un mode 144 Hz comme 143. Sans la tolérance, DrDXT harcèlerait
        // sur un écran déjà au maximum.
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 60, 144 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
    }

    [Fact]
    public void Two_hertz_is_a_real_gap()
    {
        // Garde la frontière de l'autre côté : la tolérance ne doit pas avaler
        // une vraie différence juste au-dessus d'elle.
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 145 });

        Assert.Equal(TriState.On, r.HasHeadroom);
    }

    [Fact]
    public void An_empty_mode_list_is_unknown_not_fine()
    {
        // L'échec qui compte : l'énumération n'a rien produit. Répondre « désactivé »
        // transformerait « je n'ai pas pu regarder » en « il n'y a rien de mieux ».
        var r = DisplayRefreshHeuristic.Evaluate(143, Array.Empty<int>());

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Fact]
    public void A_null_mode_list_is_unknown()
    {
        var r = DisplayRefreshHeuristic.Evaluate(143, null);

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_unreadable_current_rate_is_unknown(int currentHz)
    {
        var r = DisplayRefreshHeuristic.Evaluate(currentHz, new[] { 60, 200 });

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Fact]
    public void Never_reports_a_best_below_the_current_rate()
    {
        // Une liste de modes tronquée ou périmée ne doit pas faire dire à DrDXT que la machine tourne
        // au-dessus de ce que l'écran offre : ça se lirait comme une faute à corriger.
        var r = DisplayRefreshHeuristic.Evaluate(200, new[] { 60, 120 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
        Assert.Equal(200, r.BestHz);
    }
}
