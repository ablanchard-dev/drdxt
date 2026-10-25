using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// « DIFFERENT » N'EST pas « ENDOMMAGE », et le produit doit etre incapable de les confondre.
///
/// La tentation d'un outil de reparation est de traiter tout etat non-default comme un degat. Elle
/// est fausse et elle est dangereuse. Releve sur la machine de reference le 08/09/2026, quatre etats
/// non-default : plan Equilibre, aucun coeur parque, `LowLatencyMode` absent, 1728x1080 sur un
/// ecran 1920x1080. **Aucun des quatre n'est un defaut.** Trois sont probablement des choix, et le
/// quatrieme est le comportement par defaut de Windows.
///
/// Un produit qui « repare » ca casse le PC de quelqu'un en croyant l'aider.
///
/// La regle gravee ici : **une reparation AUTOMATIQUE exige une justification bien plus forte
/// qu'un simple ecart au default.** Seul un etat connu comme NUISIBLE l'autorise. Un etat inconnu
/// ne se repare jamais tout seul : il se signale.
/// </summary>
public class RepairFindingTests
{
    private static RepairFinding Constat(RepairStatus statut, RepairAction action) =>
        new("POWER", "plan actif", "Equilibre", "Equilibre", statut, "peu importe ici", action);

    [Fact]
    public void UN_ETAT_INCONNU_NE_SE_REPARE_JAMAIS_TOUT_SEUL()
    {
        // La regle la plus importante du Repair Engine. On ne repare pas ce qu'on ne comprend
        // pas : on le signale, et quelqu'un decide.
        var illegal = () => Constat(RepairStatus.Unknown, RepairAction.ReparationAutomatique);

        illegal.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown*");
    }

    [Fact]
    public void UN_SIMPLE_ECART_AU_DEFAULT_N_AUTORISE_PAS_UNE_REPARATION_AUTOMATIQUE()
    {
        // « Non-default » decrit une difference, pas un degat. Le seul fait qu'une valeur ne soit
        // pas celle de Windows ne dit rien de sa nocivite — ni du fait que quelqu'un l'ait voulue.
        var illegal = () => Constat(RepairStatus.NonDefault, RepairAction.ReparationAutomatique);

        illegal.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UN_CHOIX_UTILISATEUR_PROBABLE_NE_SE_TOUCHE_PAS()
    {
        var illegal = () => Constat(RepairStatus.UserChoiceProbable, RepairAction.ReparationAutomatique);

        illegal.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SEUL_UN_ETAT_CONNU_COMME_NUISIBLE_AUTORISE_LA_REPARATION_AUTOMATIQUE()
    {
        // Le controle POSITIF : la regle ne doit pas rendre le Repair Engine incapable de reparer
        // quoi que ce soit. Un etat dont on sait qu'il nuit se repare.
        var legal = Constat(RepairStatus.KnownHarmful, RepairAction.ReparationAutomatique);

        legal.Action.Should().Be(RepairAction.ReparationAutomatique);
    }

    [Fact]
    public void UN_ETAT_SUSPECT_SE_RECOMMANDE_MAIS_NE_S_APPLIQUE_PAS_SEUL()
    {
        // Suspect = « ca ressemble a une modification d'optimiseur ». C'est assez pour en parler,
        // pas assez pour ecrire sur la machine de quelqu'un.
        Constat(RepairStatus.Suspicious, RepairAction.Recommandation)
            .Action.Should().Be(RepairAction.Recommandation);

        var illegal = () => Constat(RepairStatus.Suspicious, RepairAction.ReparationAutomatique);
        illegal.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TOUT_STATUT_PEUT_ETRE_SEULEMENT_OBSERVE()
    {
        // Ne rien faire est toujours permis, et c'est le defaut. Un audit qui ne repare rien peut
        // etre excellent.
        foreach (var statut in Enum.GetValues<RepairStatus>())
            Constat(statut, RepairAction.Aucune).Action.Should().Be(RepairAction.Aucune);
    }

    [Fact]
    public void UNE_PROVENANCE_NE_S_INVENTE_PAS()
    {
        // Nommer un coupable qu'on n'a pas identifie est pire que se taire : ca donne au
        // joueur une certitude fausse, et ca nous empeche de reconnaitre plus tard qu'on n'avait
        // rien vu. `null` est la valeur normale.
        Constat(RepairStatus.NonDefault, RepairAction.Aucune)
            .Provenance.Should().BeNull();
    }

    [Fact]
    public void UN_CONSTAT_PORTE_TOUJOURS_SON_EXPLICATION()
    {
        var illegal = () => new RepairFinding(
            "POWER", "plan actif", "Equilibre", null, RepairStatus.NonDefault, "  ",
            RepairAction.Aucune);

        illegal.Should().Throw<ArgumentException>().WithMessage("*explication*");
    }

    // Un `with` ne repasse pas par les initialiseurs du constructeur : la règle doit tenir aussi par là.
    [Fact]
    public void Un_with_ne_contourne_pas_les_regles()
    {
        var inconnu = new RepairFinding("D", "n", null, null, RepairStatus.Unknown, "pourquoi");
        var nuisible = new RepairFinding("D", "n", null, null, RepairStatus.KnownHarmful, "pourquoi", RepairAction.ReparationAutomatique);

        FluentActions.Invoking(() => inconnu with { Action = RepairAction.ReparationAutomatique }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => nuisible with { Statut = RepairStatus.Unknown }).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => inconnu with { Explication = " " }).Should().Throw<ArgumentException>();
        (inconnu with { Statut = RepairStatus.Suspicious }).Statut.Should().Be(RepairStatus.Suspicious, "témoin : un with légal passe");
    }

    [Fact]
    public void La_liste_des_statuts_qui_reparent_seuls_ne_se_modifie_pas()
    {
        typeof(RepairFinding).GetField(nameof(RepairFinding.AutorisentUneReparationAutomatique))!.FieldType.IsArray
            .Should().BeFalse("un tableau public se réécrit élément par élément");
        RepairFinding.AutorisentUneReparationAutomatique.Should().Equal(RepairStatus.KnownHarmful);
    }
}
