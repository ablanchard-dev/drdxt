using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un geste du guide que DrDXT fait lui-même : un bouton sous « Comment corriger », un « Annuler » quand le geste se
// défait. Un échec se dit, et le geste reste à faire.
public sealed class GestesDuDocteurTests
{
    [Fact]
    public async Task Un_geste_fait_se_dit_fait_puis_s_annule()
    {
        var faits = new List<string>();
        var g = new GesteDuDocteur("Lancer", "détail",
            () => { faits.Add("faire"); return Task.FromResult(Result.Ok()); },
            () => { faits.Add("annuler"); return Task.FromResult(Result.Ok()); });

        g.PeutFaire.Should().BeTrue();
        await g.FaireCommand.ExecuteAsync(null);
        g.Fait.Should().BeTrue();
        g.PeutFaire.Should().BeFalse();
        g.PeutAnnuler.Should().BeTrue();

        await g.AnnulerCommand.ExecuteAsync(null);
        g.Fait.Should().BeFalse();
        g.PeutFaire.Should().BeTrue();
        faits.Should().Equal("faire", "annuler");
    }

    [Fact]
    public async Task Un_echec_se_dit_et_le_geste_reste_a_faire()
    {
        var g = new GesteDuDocteur("Lancer", "détail", () => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "non")));

        await g.FaireCommand.ExecuteAsync(null);

        g.Fait.Should().BeFalse();
        g.AEchoue.Should().BeTrue();
        g.PeutFaire.Should().BeTrue();
    }

    [Fact]
    public async Task Une_exception_est_un_echec_dit_pas_un_plantage()
    {
        var g = new GesteDuDocteur("Lancer", "détail", () => throw new InvalidOperationException("tube fermé"));

        await g.FaireCommand.ExecuteAsync(null);

        g.AEchoue.Should().BeTrue();
        g.EnCours.Should().BeFalse();
    }

    [Fact]
    public async Task Sans_annulation_aucun_annuler_n_est_propose()
    {
        var g = new GesteDuDocteur("Relancer", "détail", () => Task.FromResult(Result.Ok()));

        await g.FaireCommand.ExecuteAsync(null);

        g.Fait.Should().BeTrue();
        g.PeutAnnuler.Should().BeFalse();
    }

    [Fact]
    public void Une_ligne_de_carte_dit_si_elle_porte_des_gestes()
    {
        new EntreeCarte("windows", Severity.Info, "t", "d", "", "", false).AGestes.Should().BeFalse();
        new EntreeCarte("windows", Severity.Info, "t", "d", "", "", false,
            Gestes: [new GesteDuDocteur("x", "y", () => Task.FromResult(Result.Ok()))]).AGestes.Should().BeTrue();
    }
}
