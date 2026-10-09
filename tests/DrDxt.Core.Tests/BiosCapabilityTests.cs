using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// `GuidedBios` est l'etape actuelle, pas la finalite.
///
/// La doctrine gravee ici : **aucune ecriture BIOS generique ou non qualifiee** — et non « aucune
/// ecriture BIOS pour toujours ». Le produit doit progressivement savoir ecrire certains reglages
/// lui-meme, sur les plateformes ou une methode fiable a ete identifiee et validee.
///
/// Il n'existe volontairement aucun niveau `GenericBiosWrite`. Son absence est le garde-fou :
/// une future decision ne pourra pas s'y glisser sans ajouter explicitement une valeur a l'enum,
/// ce qui se verra en revue.
/// </summary>
public class BiosCapabilityTests
{
    private static BiosQualification Entree(
        BiosCapabilityLevel capacite = BiosCapabilityLevel.QualifiedFirmwareWrite,
        BiosRecoverability recuperation = BiosRecoverability.FirmwareRevertPossible,
        bool validee = true,
        string? valeurCourante = "3800") =>
        new("MSI B760", "7D91v1C", "memory.profile", valeurCourante, "4000",
            "provider.qualifie", capacite, recuperation, RequiresReboot: true, Validated: validee,
            // Ajoute le 08/09/2026 : cette fixture construisait des entrees VALIDEES sans
            // SOURCE, un etat que le type interdit desormais a la construction. Les cinq tests qui
            // sont tombes decrivaient donc une entree impossible — c'est la garde qui a raison, et
            // corriger la fixture est le seul remede acceptable. L'affaiblir aurait rouvert le
            // chemin qu'elle ferme : une supposition qui autorise une ecriture dans un firmware.
            DocumentationSource: "fixture de test — source fictive, exigee par la garde");

    [Fact]
    public void AUCUN_NIVEAU_D_ECRITURE_GENERIQUE_N_EXISTE()
    {
        // Le garde-fou. Si quelqu'un ajoute un jour « GenericBiosWrite », ce test tombe et la
        // discussion a lieu — au lieu que le niveau apparaisse dans un diff de 400 lignes.
        Enum.GetNames<BiosCapabilityLevel>()
            .Should().NotContain(n => n.Contains("Generic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UNE_ENTREE_QUALIFIEE_ET_VALIDEE_AUTORISE_L_ECRITURE()
    {
        // Le controle POSITIF : la doctrine ne doit pas rendre l'ecriture BIOS impossible pour
        // toujours, sinon elle n'est qu'un refus deguise.
        var entree = Entree();

        entree.EcritureAutomatiqueAutorisee.Should().BeTrue();
        entree.CeQuiManque.Should().BeNull();
    }

    [Fact]
    public void GUIDED_N_AUTORISE_JAMAIS_UNE_ECRITURE_AUTOMATIQUE()
    {
        var entree = Entree(capacite: BiosCapabilityLevel.Guided);

        entree.EcritureAutomatiqueAutorisee.Should().BeFalse();
        entree.CeQuiManque.Should().Contain("aucune voie d'ecriture qualifiee");
    }

    [Fact]
    public void UNE_MATRICE_NON_VALIDEE_N_AUTORISE_RIEN()
    {
        // Le firmware fait partie de l'empreinte materielle. Ce qui marche sur « carte A /
        // BIOS X » ne vaut pas pour « carte A / BIOS Y », encore moins pour « carte B ».
        var entree = Entree(validee: false);

        entree.EcritureAutomatiqueAutorisee.Should().BeFalse();
        entree.CeQuiManque.Should().Contain("n'a pas été validée");
    }

    [Fact]
    public void SANS_VOIE_DE_RECUPERATION_ON_N_ECRIT_PAS()
    {
        // Pour un reglage Windows, revert() tourne. Pour un reglage BIOS, si la machine ne POST
        // plus, **Windows ne s'execute pas**. Connaitre l'ancienne valeur ne sert alors a rien.
        var entree = Entree(recuperation: BiosRecoverability.RecoveryUnknown);

        entree.EcritureAutomatiqueAutorisee.Should().BeFalse();
        entree.CeQuiManque.Should().Contain("ne POST plus");
    }

    [Fact]
    public void SANS_VALEUR_COURANTE_LISIBLE_ON_N_ECRIT_PAS()
    {
        var entree = Entree(valeurCourante: null);

        entree.EcritureAutomatiqueAutorisee.Should().BeFalse();
        entree.CeQuiManque.Should().Contain("ce qu'on a changé");
    }

    [Fact]
    public void UNE_RECUPERATION_PHYSIQUE_RESTE_UNE_RECUPERATION()
    {
        // Elle exige que quelqu'un ouvre le boitier — et le joueur doit le savoir avant. Mais
        // elle ne disqualifie pas l'ecriture : la classer comme « pas de recuperation » fermerait
        // la porte a des reglages parfaitement rattrapables.
        Entree(recuperation: BiosRecoverability.PhysicalRecoveryAvailable)
            .EcritureAutomatiqueAutorisee.Should().BeTrue();
    }
}
