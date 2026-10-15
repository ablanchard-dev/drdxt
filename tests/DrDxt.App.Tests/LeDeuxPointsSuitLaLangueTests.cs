using System.Globalization;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le français met une espace avant le deux-points, l'anglais non. Un " : " écrit en dur s'affiche dans l'interface
// anglaise en « refresh rate : 200 Hz », et le regroupement des constats identiques, qui cherche ce séparateur, ne
// reconnaît alors aucun titre anglais.
public sealed class LeDeuxPointsSuitLaLangueTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static EntreeCarte E(string titre) =>
        new("windows", Severity.Major, titre, "detail", string.Empty, string.Empty, false);

    [Fact]
    public void Le_separateur_depend_de_la_langue()
    {
        using (Docteur.Portee(En)) Docteur.DeuxPoints.Should().Be(": ");
        using (Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"))) Docteur.DeuxPoints.Should().Be(" : ");
    }

    [Fact]
    public void En_anglais_les_constats_de_meme_phrase_sont_regroupes()
    {
        using var _ = Docteur.Portee(En);
        var lignes = new List<EntreeCarte>
        {
            E("Service A: its file can be replaced by another program"),
            E("Scheduled task B: its file can be replaced by another program"),
        };
        var windows = new DomaineMachine("windows", "Windows", "", EtatDomaine.ACorriger, "", "", [], lignes, [], []);

        CarteDeLaMachine.AFaire([windows]).Single().Entree.Titre
            .Should().Be("Service A, Scheduled task B: their file can be replaced by another program");
    }

    [Fact]
    public void En_anglais_un_titre_repris_dans_une_phrase_perd_son_deux_points()
    {
        using var _ = Docteur.Portee(En);
        CarteDeLaMachine.DansLaPhrase("Service A: its file can be replaced").Should().Be("service A, its file can be replaced");
    }
}
