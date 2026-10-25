using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une puce de couleur devant chaque constat, plus celles de la navigation, faisaient une page pointillée. La gravité se dit
// une fois, par le titre du groupe (« À corriger », « À regarder ») ; les lignes arrivent déjà triées, le plus grave d'abord.
public sealed class ConstatsGroupesParGraviteTests
{
    private static EntreeCarte E(string titre, Severity gravite, bool propose = false)
        => new("Windows et stabilité", gravite, titre, "Détail.", "", "", NonLu: false, Propose: propose);

    private static DomaineMachine D(params EntreeCarte[] lignes)
        => new("windows", "Windows et stabilité", "", EtatDomaine.ACorriger, "À corriger", "", [], lignes, [], []);

    [Fact]
    public void Les_constats_se_groupent_par_le_mot_de_leur_gravite()
    {
        var d = D(E("a", Severity.Major), E("b", Severity.Minor), E("c", Severity.Info));

        d.GroupesVisibles.Select(g => g.Cle + ":" + string.Join(",", g.Lignes.Select(l => l.Titre)))
            .Should().Equal("CG.ACorriger:a,b", "CG.ARegarder:c");
    }

    // Le lecteur d'écran lit ToString d'un élément de liste sans nom : celui d'un record énumérait ses champs.
    [Fact]
    public void Un_groupe_s_annonce_par_son_mot()
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        var g = D(E("a", Severity.Major)).GroupesVisibles.Single();

        g.ToString().Should().Be((string)new DrDxt.App.Converters.SeverityLabelConverter()
            .Convert(g.Premiere, typeof(string), "Titre", System.Globalization.CultureInfo.InvariantCulture));
    }

    // « Le plus urgent ici : … » répétait le premier constat, juste en dessous. Avec des constats, l'en-tête se tait ; sans
    // constat, sa phrase (« rien à signaler », « à essayer ») reste.
    [Fact]
    public void Le_verdict_ne_repete_pas_le_premier_constat()
    {
        (D(E("a", Severity.Major)) with { Verdict = "Le plus urgent ici : a." }).VerdictAffiche.Should().BeEmpty();
        (D() with { Verdict = "Rien à signaler." }).VerdictAffiche.Should().Be("Rien à signaler.");
    }

    [Fact]
    public void La_suite_repliee_garde_ses_propres_groupes()
    {
        var d = D(E("a", Severity.Major), E("b", Severity.Info), E("c", Severity.Info), E("d", Severity.Info), E("e", Severity.Info));

        d.GroupesVisibles.SelectMany(g => g.Lignes).Concat(d.GroupesReplies.SelectMany(g => g.Lignes)).Select(l => l.Titre)
            .Should().Equal("a", "b", "c", "d", "e");
        d.GroupesReplies.Should().OnlyContain(g => g.Cle == "CG.ARegarder");
    }
}
