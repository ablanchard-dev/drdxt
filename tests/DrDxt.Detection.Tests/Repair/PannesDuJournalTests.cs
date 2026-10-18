using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les pannes du journal Système, par fournisseur ET numéro, et la profondeur du journal qui borne ce qu'on peut affirmer.
public sealed class PannesDuJournalTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Depuis = Maintenant.AddDays(-45);

    private static PannesDuJournal.Releve Releve(string nom, int? n, DateTimeOffset? dernier = null)
        => new(PannesDuJournal.Familles.Single(f => f.Nom == nom), n, dernier);

    [Fact]
    public void Un_journal_plus_court_que_la_fenetre_ne_prouve_pas_aucune_panne()
    {
        var constats = PannesDuJournal.Juger([Releve("pilote graphique relance", 0)], Maintenant.AddDays(-5), Depuis, Maintenant).ToList();

        constats.Single(c => c.Nom == "profondeur du journal").Statut.Should().Be(RepairStatus.Unknown);
        constats.Single(c => c.Nom == "pilote graphique relance").Statut.Should().Be(RepairStatus.Unknown,
            "cinq jours de journal ne disent rien des quarante autres");
    }

    [Fact]
    public void Un_journal_qui_couvre_la_fenetre_dit_aucun()
        => PannesDuJournal.Juger([Releve("erreurs du disque", 0)], Maintenant.AddDays(-60), Depuis, Maintenant)
            .Single(c => c.Nom == "erreurs du disque").Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Une_famille_non_lue_se_dit_non_lue_et_une_panne_grave_merite_le_regard()
    {
        var constats = PannesDuJournal.Juger([Releve("systeme de fichiers abime", null), Releve("pilote graphique relance", 3, Maintenant.AddDays(-2)),
            Releve("pilote non charge au demarrage", 4, Maintenant)], null, Depuis, Maintenant).ToList();

        constats.Single(c => c.Nom == "systeme de fichiers abime").Statut.Should().Be(RepairStatus.Unknown);
        constats.Single(c => c.Nom == "pilote graphique relance").Statut.Should().Be(RepairStatus.Suspicious);
        constats.Single(c => c.Nom == "pilote non charge au demarrage").Statut.Should().Be(RepairStatus.Observed,
            "un pilote absent au démarrage est souvent sans suite");
    }

    // Un numéro seul attrape les événements d'information d'autres fournisseurs : chaque requête nomme son fournisseur.
    [Fact]
    public void Chaque_famille_se_lit_par_fournisseur_et_numero()
    {
        foreach (var f in PannesDuJournal.Familles)
            PannesDuJournal.Requete(f, Depuis).Should().Contain("Provider[@Name=").And.Contain("EventID=");
        PannesDuJournal.Requete(PannesDuJournal.Familles.Single(f => f.Ids.Contains(4101)), Depuis)
            .Should().Contain("@Name='Display'", "4101 vient du fournisseur Display, pas du pilote de la carte");
    }
}
