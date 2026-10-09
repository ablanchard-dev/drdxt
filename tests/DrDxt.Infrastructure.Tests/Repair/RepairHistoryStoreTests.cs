using DrDxt.Core;
using DrDxt.Infrastructure.Repair;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests.Repair;

/// <summary>
/// Le format sur disque du journal des constats — ce que le convertisseur d'enumeration tient
/// vraiment, et ce qu'il ne tient pas.
///
/// Le commentaire de <c>RepairHistoryStore.Options</c> promet deux choses et n'en prouvait
/// aucune : « un journal se relit dans six mois » et « un entier deviendrait faux au premier ajout
/// dans l'enumeration ». Retirer le convertisseur laissait toute la suite VERTE — l'aller-retour
/// d'entiers marche parfaitement tant que l'enumeration ne bouge pas, et aucun test ne regardait ce
/// qui etait ecrit.
///
/// Ce qu'on a cru y trouver et qui n'y est pas : la tolerance a un statut inconnu. Le
/// convertisseur ne rattrape rien d'une version qui aurait ajoute un statut — le journal entier
/// devient illisible, pas seulement l'entree fautive. Le deuxieme test fige ce comportement reel
/// plutot que celui qu'on esperait, parce que ce qui compte en prod est ailleurs : un journal
/// illisible n'est jamais EFFACE.
/// </summary>
public class RepairHistoryStoreTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-histo-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_chemin)) File.Delete(_chemin); } catch { }
    }

    private static RepairSnapshot Photo(RepairStatus statut, string domaine = "POWER")
        => new(DateTimeOffset.UnixEpoch, [domaine],
            [new RepairEtat(domaine, "un reglage", "une valeur", statut)]);

    [Fact]
    public void Le_statut_est_ecrit_en_TEXTE_et_pas_en_position_dans_l_enumeration()
    {
        var store = new RepairHistoryStore(_chemin);
        store.Enregistrer(Photo(RepairStatus.Suspicious)).IsOk.Should().BeTrue();

        var ecrit = File.ReadAllText(_chemin);

        // C'est la seule moitie de la promesse qui soit VERIFIABLE sans changer l'enumeration :
        // ce que le produit pose sur le disque est un NOM. Un nom survit a une insertion dans
        // l'enumeration ; une position, non.
        ecrit.Should().Contain("\"Statut\": \"Suspicious\"",
            "un journal se relit dans six mois, et un nom ne depend pas de l'ordre des membres");
        ecrit.Should().NotContain($"\"Statut\": {(int)RepairStatus.Suspicious}");

        // Et voici pourquoi l'entier serait faux : un journal qui aurait garde la POSITION se relit
        // aujourd'hui `Suspicious` — et dirait `KnownHarmful` le jour ou un membre serait insere
        // avant lui. Le produit accuserait alors une machine sur un decalage de numerotation.
        File.WriteAllText(_chemin, ecrit.Replace("\"Suspicious\"", ((int)RepairStatus.Suspicious).ToString()));

        store.Lire().Value!.Single().Etats.Single().Statut.Should().Be(RepairStatus.Suspicious,
            "la position se relit sans broncher : rien ne signalerait le decalage");
    }

    [Fact]
    public void Un_statut_INCONNU_rend_tout_le_journal_illisible_et_ne_l_efface_PAS()
    {
        // Deux photos ecrites par le vrai chemin d'ecriture, puis un statut qu'une version future
        // aurait ajoute et que celle-ci ne connait pas.
        var store = new RepairHistoryStore(_chemin);
        store.Enregistrer(Photo(RepairStatus.ExpectedDefault)).IsOk.Should().BeTrue();
        store.Enregistrer(Photo(RepairStatus.Observed, "CPU")).IsOk.Should().BeTrue();

        var versionFuture = File.ReadAllText(_chemin).Replace("\"Observed\"", "\"BientotNuisible\"");
        File.WriteAllText(_chemin, versionFuture);

        var lu = store.Lire();

        // L'entree valide ne survit pas. La deserialisation est faite d'un bloc : un seul nom
        // inconnu emporte tout le fichier. Le convertisseur n'y change rien — il n'a jamais promis
        // ca. C'est dit ici pour qu'on cesse de le croire.
        lu.IsOk.Should().BeFalse("un nom inconnu fait echouer la lecture du fichier ENTIER");
        lu.Error!.Value.Message.Should().Contain("illisible",
            "et c'est nomme : une lecture ratee ne se confond pas avec un journal vide");

        // Ce qui compte en prod, et qui tient : rien n'est efface. Un journal illisible reste
        // sur le disque intact, donc une version qui saurait le lire le retrouverait entier.
        store.Enregistrer(Photo(RepairStatus.NonDefault)).IsOk.Should().BeFalse(
            "ecraser un journal casse effacerait l'historique qu'on essaie de tenir");
        File.ReadAllText(_chemin).Should().Be(versionFuture, "le fichier n'a pas ete touche");
    }
}
