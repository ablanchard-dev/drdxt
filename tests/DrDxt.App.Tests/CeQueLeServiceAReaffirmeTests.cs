using DrDxt.App.ViewModels;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// SEC3 (08/10/2026) : après une mise à jour de Windows, le service réaffirme seul ce que le joueur a gardé. Il ne faisait
/// que le journaliser ; la page Windows le dit maintenant, réglage par réglage, avec la date.
/// </summary>
public sealed class CeQueLeServiceAReaffirmeTests
{
    private static readonly DateTimeOffset Quand = new(2026, 10, 9, 8, 30, 0, TimeSpan.Zero);

    private static string Loc(string k) => k == "Service.Reaffirme" ? "Service.Reaffirme {0} {1}" : k;

    private static ReconcilerState Etat(params (string Id, string Code)[] items) => ReconcilerState.Default with
    {
        Items = items.Select(i => new ReconcilerItemState(i.Id, false, "26200", "", Quand, i.Code)).ToList(),
    };

    [Fact]
    public void Un_reglage_reaffirme_se_dit_avec_son_nom()
    {
        var lignes = CarteDeLaMachine.LignesDuService(Etat(("os.hags", "Reapplied"), ("fixit.sanePowerPlan", "InSync")), Loc);

        lignes.Should().ContainSingle().Which.Should().Contain("Service.Reaffirme").And.Contain("tweak.latency.hagsOn.title");
    }

    [Fact]
    public void Rien_de_reaffirme_ne_dit_rien()
    {
        CarteDeLaMachine.LignesDuService(Etat(("os.hags", "InSync")), k => k).Should().BeEmpty();
        CarteDeLaMachine.LignesDuService(null, k => k).Should().BeEmpty("état du service non lu ou absent");
    }

    [Fact]
    public void Un_reglage_inconnu_garde_son_identifiant_plutot_que_rien()
        => CarteDeLaMachine.LignesDuService(Etat(("nouveau.reglage", "Reapplied")), Loc).Single()
            .Should().Contain("nouveau.reglage");
}
