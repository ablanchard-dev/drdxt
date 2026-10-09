using DrDxt.Core;
using DrDxt.Infrastructure.Repair;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests.Repair;

/// <summary>
/// Ce qui a change depuis la derniere fois, et ce qui ne compte pas comme un changement.
///
/// Le piege que ces tests figent : un diff qui montre les valeurs MESUREES trouve toujours
/// quelque chose — frequence CPU, debit RAM, temperature — parce qu'elles varient par nature. Un
/// diff qui trouve toujours quelque chose est un diff qu'on cesse de lire, et le jour ou il a une
/// vraie nouvelle, personne ne la voit.
/// </summary>
public class RepairJournalServiceTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_chemin)) File.Delete(_chemin); } catch { }
    }

    private RepairJournalService Service(ConditionsDeMesure? conditions = null)
        => new(new RepairHistoryStore(_chemin), () => conditions);

    private static RepairFinding Constat(string domaine, string nom, string? valeur, RepairStatus statut)
        => new(domaine, nom, valeur, null, statut, "parce que.");

    // 26/09/2026 : cette phrase est affichee. Sa jumelle du ViewModel avait ete corrigee le 17/09
    // (accents, pas de capitales d'insistance) ; celle-ci, hors de la garde des ViewModels, non.
    [Fact]
    public void Un_historique_illisible_se_dit_inconnu_en_francais_correct()
    {
        File.WriteAllText(_chemin, "pas du json");

        var journal = Service().EnregistrerEtComparer(
            [Constat("POWER", "Plan", "Équilibré", RepairStatus.ExpectedDefault)]);

        journal!.Avertissement.Should().Contain("Ce qui a changé depuis la dernière fois est inconnu, pas vide.");
    }

    // 26/09/2026 : l'historique se relit sans controle par entree. Un fichier ampute (entree nulle, photo
    // sans ses listes) ne doit jamais faire tomber le scan : il se dit illisible, comme un JSON casse.
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Quand\":\"2026-09-01T00:00:00+00:00\"}]")]
    [InlineData("[{\"Quand\":\"2026-09-01T00:00:00+00:00\",\"DomainesExamines\":[\"POWER\"],\"Etats\":[null]}]")]
    public void Un_historique_ampute_ne_fait_pas_tomber_le_scan(string contenu)
    {
        File.WriteAllText(_chemin, contenu);

        var act = () => Service().EnregistrerEtComparer(
            [Constat("POWER", "minimum du processeur", "5%", RepairStatus.ExpectedDefault)]);

        act.Should().NotThrow();
    }

    [Fact]
    public void La_PREMIERE_photo_n_est_pas_un_changement()
    {
        var journal = Service().EnregistrerEtComparer(
            [Constat("POWER", "minimum du processeur", "5%", RepairStatus.ExpectedDefault)]);

        journal.Should().BeNull("il n'y a rien a comparer, et une premiere photo n'est pas une nouvelle");
        File.Exists(_chemin).Should().BeTrue("elle doit quand meme etre enregistree pour la prochaine fois");
    }

    [Fact]
    public void Un_changement_de_JUGEMENT_est_une_nouvelle()
    {
        var service = Service();
        service.EnregistrerEtComparer(
            [Constat("POWER", "minimum du processeur", "5%", RepairStatus.ExpectedDefault)]);

        var journal = service.EnregistrerEtComparer(
            [Constat("POWER", "minimum du processeur", "100%", RepairStatus.NonDefault)]);

        journal!.Nouvelles.Should().ContainSingle()
            .Which.Nom.Should().Be("minimum du processeur");
    }

    [Fact]
    public void Une_valeur_MESUREE_qui_bouge_n_est_PAS_une_nouvelle()
    {
        // Le cas qui a rendu le premier journal illisible : quatre « changements » entre deux
        // passes a une minute d'ecart, tous exacts, tous sans interet.
        var service = Service();
        service.EnregistrerEtComparer(
            [Constat("CPU", "frequence effective", "3014 MHz", RepairStatus.Observed)]);

        var journal = service.EnregistrerEtComparer(
            [Constat("CPU", "frequence effective", "4493 MHz", RepairStatus.Observed)]);

        journal!.Nouvelles.Should().BeEmpty(
            "un diff qui trouve toujours quelque chose est un diff qu'on cesse de lire");
    }

    [Fact]
    public void Une_famille_QUI_APPARAIT_est_ETIQUETEE_et_n_accuse_pas_la_machine()
    {
        // Le jour ou l'audit apprend a lire une famille de plus, ce n'est pas la machine qui a
        // change : c'est le produit qui a appris a regarder.
        var service = Service();
        service.EnregistrerEtComparer([Constat("POWER", "plan", "Equilibre", RepairStatus.Observed)]);

        var journal = service.EnregistrerEtComparer(
        [
            Constat("POWER", "plan", "Equilibre", RepairStatus.Observed),
            Constat("STOCKAGE", "usure", "0 %", RepairStatus.Observed),
        ]);

        // Et le depot est plus fin que mon hypothese. J'attendais que la famille neuve soit
        // ECARTEE. Elle ne l'est pas : elle est ETIQUETEE `FamilleNouvelle`, avec une phrase qui
        // dit que c'est le produit qui a appris a lire. C'est mieux — un constat ecarte ne
        // s'explique pas, un constat etiquete si.
        var stockage = journal!.Nouvelles.Should().ContainSingle(d => d.Domaine == "STOCKAGE").Subject;
        stockage.Quoi.Should().Be(RepairChangement.FamilleNouvelle,
            "et surtout PAS `Apparu` : la machine n'a rien gagne, c'est le produit qui a appris");
        stockage.Phrase.Should().Contain("pas la machine qui a changé");
    }

    [Fact]
    public void Un_constat_qui_DISPARAIT_dans_une_famille_deja_lue_EST_une_nouvelle()
    {
        var service = Service();
        service.EnregistrerEtComparer(
        [
            Constat("BCD", "bootstatuspolicy", "IgnoreAllFailures", RepairStatus.Suspicious),
            Constat("BCD", "hypervisorlaunchtype", "Auto", RepairStatus.ExpectedDefault),
        ]);

        var journal = service.EnregistrerEtComparer(
            [Constat("BCD", "hypervisorlaunchtype", "Auto", RepairStatus.ExpectedDefault)]);

        journal!.Nouvelles.Should().ContainSingle()
            .Which.Nom.Should().Be("bootstatuspolicy");
    }

    [Fact]
    public void Aucun_constat_ne_produit_aucune_photo()
    {
        Service().EnregistrerEtComparer([]).Should().BeNull();
        Service().EnregistrerEtComparer(null).Should().BeNull();
        File.Exists(_chemin).Should().BeFalse("une photo vide ne vaut pas une photo");
    }

    [Fact]
    public void Les_CONDITIONS_de_la_photo_d_AVANT_survivent_au_disque()
    {
        // L'avertissement de conditions est la seule chose qui empeche le journal d'accuser une
        // machine qui n'a rien fait (09/09 : « le multi-canal n'est plus prouve », alors qu'une
        // compilation tournait). Il se calcule sur la photo PRECEDENTE, donc sur ce qui est RELU du
        // disque — et aucun test ne faisait ecrire des conditions par le produit : toutes les passes
        // partaient de `null`. La branche entiere etait verte sans jamais avoir traverse un fichier.
        //
        // Seule la photo d'avant est agitee ici : si ses conditions ne survivent pas a
        // l'aller-retour, il n'y a plus aucune raison d'avertir, et le produit se tait.
        ConditionsDeMesure? conditions = new(10, 12.6);
        var service = new RepairJournalService(new RepairHistoryStore(_chemin), () => conditions);

        service.EnregistrerEtComparer([Constat("RAM", "mode memoire", "Double canal", RepairStatus.ExpectedDefault)]);

        conditions = new ConditionsDeMesure(10, 1.4);   // la machine s'est calmee : rien a dire sur celle-CI
        var journal = service.EnregistrerEtComparer(
            [Constat("RAM", "mode memoire", "Inconnu", RepairStatus.Unknown)]);

        journal!.Avertissement.Should().NotBeNull("la photo d'avant a ete prise sur une machine agitee")
            .And.Contain("le banc mémoire du scan précédent", "et c'est bien lui qui etait agite, pas celui-ci");
    }

    [Fact]
    public void Une_mise_a_jour_du_lecteur_entre_deux_photos_est_dite_et_survit_au_disque()
    {
        // 16/09/2026 : « arrets brutaux 11 → 10 » venait du produit, pas de la machine. L'empreinte
        // du lecteur est ecrite avec la photo et RELUE du disque au passage suivant.
        new RepairJournalService(new RepairHistoryStore(_chemin), () => null, empreinteLecteur: "ancien")
            .EnregistrerEtComparer([Constat("STABILITE", "arrets brutaux", "11 sur 90 jours", RepairStatus.Suspicious)]);

        var journal = new RepairJournalService(new RepairHistoryStore(_chemin), () => null, empreinteLecteur: "nouveau")
            .EnregistrerEtComparer([Constat("STABILITE", "arrets brutaux", "10 sur 90 jours", RepairStatus.Observed)]);

        journal!.Avertissement.Should().NotBeNull().And.Contain("mis à jour");
    }

    [Fact]
    public void Sans_rien_de_change_il_n_y_a_rien_a_nuancer()
    {
        // 23/09/2026, scan réel : « Ce qui a changé depuis la dernière fois » s'affichait avec un
        // compteur à 0, un chevron qui n'ouvrait rien, et en orange « DrDXT a été mis à jour entre
        // les deux photos. Un changement de valeur ou de jugement peut venir de sa nouvelle façon de
        // lire ». Une mise en garde sur des changements qui n'existent pas. La mise à jour du lecteur
        // et l'agitation de la machine NUANCENT des changements ; sans changement, elles se taisent.
        ConditionsDeMesure? conditions = new(10, 12.6);
        new RepairJournalService(new RepairHistoryStore(_chemin), () => conditions, empreinteLecteur: "ancien")
            .EnregistrerEtComparer([Constat("RAM", "mode memoire", "Double canal", RepairStatus.ExpectedDefault)]);

        conditions = new ConditionsDeMesure(10, 1.4);
        var journal = new RepairJournalService(new RepairHistoryStore(_chemin), () => conditions, empreinteLecteur: "nouveau")
            .EnregistrerEtComparer([Constat("RAM", "mode memoire", "Double canal", RepairStatus.ExpectedDefault)]);

        journal!.Nouvelles.Should().BeEmpty("témoin : rien n'a changé entre les deux photos");
        journal.Avertissement.Should().BeNull();
    }

    [Fact]
    public void Ce_qu_une_photo_sans_droits_n_a_pas_pu_lire_ne_passe_pas_pour_un_changement()
    {
        // 16/09/2026, capture d'écran : la photo d'avant venait d'un outil lance en administrateur,
        // celle d'apres de l'appli sans droits. La famille BCD « disparue » et la latence DPC
        // « Suspicious → Unknown » n'etaient que des lectures impossibles.
        new RepairJournalService(new RepairHistoryStore(_chemin), () => null, eleve: true)
            .EnregistrerEtComparer([
                Constat("BCD", "bootstatuspolicy", "IgnoreAllFailures", RepairStatus.NonDefault),
                Constat("LATENCE", "DPC/ISR des pilotes", "3 pilotes", RepairStatus.Suspicious),
                Constat("POWER", "plan d'alimentation actif", "Equilibre", RepairStatus.ExpectedDefault)]);

        var journal = new RepairJournalService(new RepairHistoryStore(_chemin), () => null, eleve: false)
            .EnregistrerEtComparer([
                Constat("LATENCE", "DPC/ISR des pilotes", null, RepairStatus.Unknown),
                Constat("POWER", "plan d'alimentation actif", "Hautes performances", RepairStatus.UserChoiceProbable)]);

        journal!.Avertissement.Should().Contain("administrateur");
        journal.Nouvelles.Should().NotContain(d => d.Domaine == "BCD", "une lecture refusee n'est pas une disparition");
        journal.Nouvelles.Should().NotContain(d => d.Domaine == "LATENCE", "une lecture refusee n'est pas un changement de jugement");
        journal.Nouvelles.Should().Contain(d => d.Domaine == "POWER", "un vrai changement reste dit");
    }

    [Fact]
    public void Des_conditions_illisibles_ne_font_pas_echouer_le_journal()
    {
        var service = new RepairJournalService(
            new RepairHistoryStore(_chemin),
            () => throw new InvalidOperationException("compteur indisponible"));

        var acte = () => service.EnregistrerEtComparer(
            [Constat("CPU", "frequence", "3000 MHz", RepairStatus.Observed)]);

        acte.Should().NotThrow("ne pas connaitre l'etat de la machine est un resultat ; "
                               + "faire echouer le scan pour ca n'en est pas un");
    }
}
