using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Un defaut qui n'a besoin d'aucune reference.
///
/// Tout le reste de l'audit bute sur la meme difficulte : pour dire qu'une valeur devie, il faut
/// connaitre le defaut. « Ce service n'a pas pu demarrer » echappe a cette contrainte — c'est un
/// fait auto-decrit, et le systeme l'ecrit lui-meme.
///
/// La regression que ces tests empechent : qu'un futur lot elargisse la liste des identifiants a
/// des evenements qui ne sont pas des echecs (un service arrete proprement, desactive, ou demarre
/// a la demande), ce qui remplirait le rapport de bruit et ferait passer un fonctionnement normal
/// pour un degat.
/// </summary>
public class ServiceFailureLogTests
{
    [Fact]
    public void LA_LECTURE_ABOUTIT_SUR_CETTE_MACHINE()
    {
        // Ce test EXECUTE la requete sur le vrai journal. Un test qui se contenterait d'un
        // journal simule ne dirait rien de la requete XPath, qui est la seule chose fragile ici.
        var r = new WindowsServiceFailureLog().Depuis(DateTimeOffset.Now.AddDays(-30));

        r.IsOk.Should().BeTrue("le journal System est lisible sans elevation");
        r.Value.Should().NotBeNull();
    }

    [Fact]
    public void UN_SERVICE_N_EST_JAMAIS_RENDU_SANS_NOM_NI_SANS_OCCURRENCE()
    {
        // Un constat sans nom ne peut pas etre agi, et une occurrence a zero n'a aucun sens :
        // les deux signaleraient que le regroupement a derive.
        foreach (var f in new WindowsServiceFailureLog().Depuis(DateTimeOffset.Now.AddDays(-30)).Value!)
        {
            f.Service.Should().NotBeNullOrWhiteSpace();
            f.Occurrences.Should().BeGreaterThan(0);
            f.Nature.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void LA_FENETRE_EST_RESPECTEE_ET_N_INVENTE_RIEN()
    {
        // Le controle qui compte. Une fenetre d'un jour ne peut pas contenir plus d'echecs
        // qu'une fenetre de trente : si c'etait le cas, le filtre de date ne filtrerait rien et
        // le produit annoncerait des incidents recents qui datent de l'an dernier.
        var log = new WindowsServiceFailureLog();
        var trenteJours = log.Depuis(DateTimeOffset.Now.AddDays(-30)).Value!.Sum(f => f.Occurrences);
        var unJour = log.Depuis(DateTimeOffset.Now.AddDays(-1)).Value!.Sum(f => f.Occurrences);

        unJour.Should().BeLessThanOrEqualTo(trenteJours);
    }

    [Fact]
    public void UNE_FENETRE_DANS_LE_FUTUR_NE_REND_RIEN()
    {
        // Le controle NEGATIF : sans lui, un filtre de date casse passerait inapercu tant que la
        // machine a des echecs a montrer.
        new WindowsServiceFailureLog().Depuis(DateTimeOffset.Now.AddDays(1))
            .Value.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Une rafale n'est pas une maladie.
    //
    // Releve sur la machine de reference : « 21 echecs » pour `atvi-skuld`, tous entre 18:14 et 18:16
    // le meme jour, avec ERROR_ALREADY_EXISTS : un installeur qui reessaie, pour un service qui
    // n'existe meme plus. Classer ce service suspect sur le compte seul serait exact et faux.
    //
    // Compter sans regarder la repartition transforme un accident en condition chronique. C'est
    // une facon nouvelle de mentir avec un chiffre juste, et c'est celle que ces tests ferment.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static ServiceFailure Echec(int occurrences, int jours, TimeSpan etendue) =>
        new("un-service", 7000, occurrences, jours,
            DateTimeOffset.Now - etendue, DateTimeOffset.Now, "n'a pas pu demarrer");

    [Fact]
    public void VINGT_ET_UN_ECHECS_EN_DEUX_MINUTES_LE_MEME_JOUR_SONT_UNE_RAFALE()
    {
        // Le cas reel qui a revele le defaut, reproduit tel quel.
        var f = Echec(occurrences: 21, jours: 1, etendue: TimeSpan.FromMinutes(2));

        f.EstUneRafale.Should().BeTrue();
        f.EstRecurrent.Should().BeFalse("tout tient dans une seule journee");
    }

    [Fact]
    public void CINQ_ECHECS_SUR_CINQ_JOURS_SONT_UNE_CONDITION_QUI_REVIENT()
    {
        // Le controle qui empeche la sur-correction. Moins d'occurrences que la rafale, et
        // pourtant bien plus inquietant : c'est la REPARTITION qui porte le sens. Sans ce test,
        // on pourrait « corriger » en exigeant beaucoup d'occurrences et rendre le lecteur aveugle
        // au seul cas qui merite un regard.
        var f = Echec(occurrences: 5, jours: 5, etendue: TimeSpan.FromDays(5));

        f.EstRecurrent.Should().BeTrue();
        f.EstUneRafale.Should().BeFalse();
    }

    [Fact]
    public void UN_ECHEC_ISOLE_N_EST_NI_RAFALE_NI_RECURRENCE()
    {
        var f = Echec(occurrences: 1, jours: 1, etendue: TimeSpan.Zero);

        f.EstUneRafale.Should().BeFalse("une seule occurrence n'est pas une rafale");
        f.EstRecurrent.Should().BeFalse();
    }

    [Fact]
    public void LE_CLASSEMENT_MET_LA_RECURRENCE_AVANT_LE_COMPTE()
    {
        // Sur la machine reelle : si un service recurrent existait, il devrait passer devant
        // `atvi-skuld` et ses 21 occurrences d'un seul jour. On verifie l'ordre, pas le contenu.
        var f = new WindowsServiceFailureLog().Depuis(DateTimeOffset.Now.AddDays(-40)).Value!;

        f.Select(x => x.JoursDistincts).Should().BeInDescendingOrder();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Le regroupement lui-meme, avec des dates choisies.
    //
    // Les tests ci-dessus construisaient des `ServiceFailure` a la main : ils eprouvaient les
    // proprietes et jamais le CALCUL qui les remplit. Une mutation l'a prouve — remplacer le
    // compte de journees distinctes par le compte brut n'a tue aucun test. Deuxieme garantie de
    // la journee nommee sans etre prouvee.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static (string, int, DateTimeOffset) Ev(string jour, string heure) =>
        ("svc", 7000, DateTimeOffset.Parse($"2026-09-{jour}T{heure}:00+02:00"));

    [Fact]
    public void VINGT_ET_UN_EVENEMENTS_DU_MEME_JOUR_NE_FONT_QU_UNE_JOURNEE()
    {
        // Le cas `atvi-skuld`, reconstitue a la minute : 21 evenements entre 18:14 et 18:16.
        var evs = Enumerable.Range(0, 21).Select(i => Ev("01", $"18:{14 + i % 3:00}"));

        var f = WindowsServiceFailureLog.Regrouper(evs).Single();

        f.Occurrences.Should().Be(21);
        f.JoursDistincts.Should().Be(1, "tout tient dans la journee du 01/09");
        f.EstUneRafale.Should().BeTrue();
        f.EstRecurrent.Should().BeFalse();
    }

    [Fact]
    public void TROIS_EVENEMENTS_SUR_TROIS_JOURS_FONT_TROIS_JOURNEES()
    {
        // Le controle qui tue la mutation : ici occurrences et journees valent 3, donc un test
        // qui ne regarderait que l'egalite passerait. C'est le test PRECEDENT, ou 21 != 1, qui
        // separe les deux calculs — les deux sont necessaires.
        var f = WindowsServiceFailureLog.Regrouper([Ev("01", "10:00"), Ev("03", "11:00"), Ev("05", "12:00")])
            .Single();

        f.Occurrences.Should().Be(3);
        f.JoursDistincts.Should().Be(3);
        f.EstRecurrent.Should().BeTrue();
        f.EstUneRafale.Should().BeFalse();
    }

    [Fact]
    public void DEUX_SERVICES_NE_SE_MELANGENT_PAS_ET_LE_RECURRENT_PASSE_DEVANT()
    {
        // Une rafale de 10 le meme jour ne doit pas passer devant une panne de 3 sur 3 jours.
        var rafale = Enumerable.Range(0, 10).Select(_ => ("rafale", 7000, DateTimeOffset.Parse("2026-09-01T18:15:00+02:00")));
        var chronique = new[] { ("chronique", 7000, DateTimeOffset.Parse("2026-09-01T10:00:00+02:00")),
                                ("chronique", 7000, DateTimeOffset.Parse("2026-09-03T10:00:00+02:00")),
                                ("chronique", 7000, DateTimeOffset.Parse("2026-09-05T10:00:00+02:00")) };

        var r = WindowsServiceFailureLog.Regrouper(rafale.Concat(chronique));

        r.Should().HaveCount(2);
        r[0].Service.Should().Be("chronique", "la recurrence passe avant le compte");
        r[1].Occurrences.Should().Be(10);
    }

    // La lecture s'arrête aux 500 événements les plus récents. Un service qui plante toutes les minutes depuis trente jours
    // tient alors en huit heures : lu ainsi, une seule journée, il passait pour une rafale d'installeur. Une lecture tronquée
    // ne connaît pas l'étendue : ni rafale, et le constat le dit.
    [Fact]
    public void Une_lecture_tronquee_ne_passe_jamais_pour_une_rafale()
    {
        var debut = new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.Zero);
        var boucle = Enumerable.Range(0, 500).Select(i => ("boucle", 7031, debut.AddMinutes(i)));

        var f = WindowsServiceFailureLog.Regrouper(boucle, tronque: true).Single();

        f.Tronque.Should().BeTrue();
        f.EstUneRafale.Should().BeFalse();
    }

    [Fact]
    public void Une_lecture_complete_garde_la_rafale()
    {
        var debut = new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.Zero);
        var rafale = Enumerable.Range(0, 21).Select(i => ("atvi-skuld", 7023, debut.AddSeconds(i)));

        WindowsServiceFailureLog.Regrouper(rafale, tronque: false).Single().EstUneRafale.Should().BeTrue("témoin");
    }
}
