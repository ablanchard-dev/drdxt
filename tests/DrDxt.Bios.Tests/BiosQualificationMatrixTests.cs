using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// La matrice existe pour refuser, pas pour autoriser.
///
/// Le seul chemin qui mene a une ecriture dans un firmware passe par
/// `EcritureAutomatiqueAutorisee`. Ces tests gravent les deux facons de le forcer sans le vouloir :
/// declarer une entree validee sans dire d'ou vient la certitude, et rendre une entree
/// « approchante » quand la combinaison exacte n'est pas connue.
/// </summary>
public class BiosQualificationMatrixTests
{
    [Fact]
    public void UNE_ENTREE_VALIDEE_SANS_SOURCE_EST_REFUSEE_A_LA_CONSTRUCTION()
    {
        // `Validated` est le seul champ qui debloque l'ecriture. Le poser sans source rend la
        // matrice inverifiable par quelqu'un d'autre — et c'est ainsi qu'une supposition finit par
        // autoriser une ecriture dans un firmware. La regle vit dans le constructeur, pas dans une
        // revue de code qui peut etre distraite.
        var construire = () => new BiosQualification(
            Board: "carte", FirmwareVersion: "1.0", Setting: "XMP",
            CurrentValue: "Disabled", TargetValue: "Profil 1", Provider: "vendeur",
            Capability: BiosCapabilityLevel.VendorSupportedWrite,
            Recoverability: BiosRecoverability.PhysicalRecoveryAvailable,
            Validated: true,
            DocumentationSource: null);

        construire.Should().Throw<ArgumentException>().WithMessage("*source*");
    }

    [Fact]
    public void LA_MEME_ENTREE_AVEC_SA_SOURCE_EST_ACCEPTEE_ET_AUTORISE_L_ECRITURE()
    {
        // Le controle POSITIF : sans lui, la garde pourrait tout refuser et personne ne le verrait.
        new BiosQualification(
            Board: "carte", FirmwareVersion: "1.0", Setting: "XMP",
            CurrentValue: "Disabled", TargetValue: "Profil 1", Provider: "vendeur",
            Capability: BiosCapabilityLevel.VendorSupportedWrite,
            Recoverability: BiosRecoverability.PhysicalRecoveryAvailable,
            Validated: true,
            DocumentationSource: "manuel constructeur, page 42")
            .EcritureAutomatiqueAutorisee.Should().BeTrue();
    }

    [Fact]
    public void UNE_ENTREE_NON_VALIDEE_SANS_SOURCE_RESTE_LEGITIME()
    {
        // La source n'est exigee que par la VALIDATION. Une entree honnetement incomplete doit
        // pouvoir exister : c'est l'etat normal de la connaissance, et l'interdire pousserait a
        // cocher `Validated` pour se debarrasser de l'erreur.
        var e = new BiosQualification(
            Board: "carte", FirmwareVersion: "1.0", Setting: "XMP",
            CurrentValue: null, TargetValue: "Profil 1", Provider: "aucun",
            Capability: BiosCapabilityLevel.Guided,
            Recoverability: BiosRecoverability.RecoveryUnknown);

        e.Validated.Should().BeFalse();
        e.EcritureAutomatiqueAutorisee.Should().BeFalse();
    }

    [Theory]
    [InlineData("MSI PRO B760-P WIFI DDR4 (MS-7D98)", "1.G0")] // meme carte, autre firmware
    [InlineData("MSI PRO B760-A WIFI (MS-7E07)", "1.H0")]      // autre carte, meme firmware
    [InlineData(null, "1.H0")]
    [InlineData("MSI PRO B760-P WIFI DDR4 (MS-7D98)", null)]
    public void UNE_COMBINAISON_QUI_N_EST_PAS_DANS_LA_MATRICE_NE_REND_RIEN_D_APPROCHANT(
        string? carte, string? firmware)
    {
        // LE CŒUR DU TYPE. Une entree valable sur « carte A / BIOS X » ne devient pas valable
        // sur « carte A / BIOS Y ». Rendre l'entree voisine « parce qu'elle est proche » est
        // exactement la faute que la matrice existe pour empecher — et c'est la faute la plus
        // tentante, puisqu'elle rend le produit plus bavard.
        BiosQualificationMatrix.Pour(carte, firmware).Should().BeEmpty();
    }

    [Fact]
    public void LA_CARTE_REELLE_EST_CONNUE_ET_AUCUNE_DE_SES_ENTREES_N_AUTORISE_UNE_ECRITURE()
    {
        var entrees = BiosQualificationMatrix.Pour("MSI PRO B760-P WIFI DDR4 (MS-7D98)", "1.H0");

        entrees.Should().NotBeEmpty("la carte de reference a ete mesuree le 08/09/2026");
        entrees.Should().OnlyContain(e => !e.EcritureAutomatiqueAutorisee);
        entrees.Should().OnlyContain(e => e.Capability == BiosCapabilityLevel.Guided);

        // La raison du refus compte autant que le refus : elle doit nommer la recuperation,
        // parce que c'est elle qu'il faudra etablir pour avancer. Une entree qui refuserait pour
        // une autre raison signalerait que la matrice a derive.
        entrees.Should().OnlyContain(e => e.CeQuiManque != null);
    }

    [Fact]
    public void LE_RISQUE_DE_NE_PAS_DEMARRER_EST_DIT_MEME_QUAND_ON_NE_SAIT_PAS_RECUPERER()
    {
        // Les deux champs repondent a deux questions differentes : « peut-on y arriver ? » et
        // « comment revient-on ? ». Les fondre laisserait le joueur cliquer sur un XMP sans savoir
        // qu'un ecran noir est un resultat NORMAL de la manœuvre.
        BiosQualificationMatrix.Pour("MSI PRO B760-P WIFI DDR4 (MS-7D98)", "1.H0")
            .Should().Contain(e => e.Setting == "XMP"
                                && e.NoPost == NoPostRisk.CanPreventPost
                                && e.Recoverability == BiosRecoverability.RecoveryUnknown);
    }

    [Fact]
    public void LA_MATRICE_DIT_OU_LIRE_MAIS_NE_FIGE_JAMAIS_LA_VALEUR_COURANTE()
    {
        // La valeur courante se LIT a l'execution (NvramBiosSettingsReader). La
        // figer ici ferait croire a une lecture qui n'a pas eu lieu sur cette machine. Un
        // mecanisme de lecture ne se declare qu'avec l'emplacement releve dans l'IFR.
        BiosQualificationMatrix.Pour("MSI PRO B760-P WIFI DDR4 (MS-7D98)", "1.H0")
            .Should().OnlyContain(e => e.CurrentValue == null
                                    && (e.ReadMechanism == null) == (e.Nvram == null));
    }

    [Fact]
    public void TOUS_LES_MANQUES_SONT_DITS_ET_NON_SEULEMENT_LE_PREMIER()
    {
        // `CeQuiManque` rendait un motif : le premier masquait les autres. Sur la carte reelle
        // il affichait « aucune voie d'ecriture qualifiee » — vrai, mais il taisait que la voie de
        // RECUPERATION est inconnue et que la valeur courante est illisible. On croyait un
        // obstacle la ou il y en a trois, ce qui donne une feuille de route fausse.
        //
        // Meme faute que `CandidateAppraisal.Motifs`, commise en parallele dans ce type.
        var e = BiosQualificationMatrix.Pour("MSI PRO B760-P WIFI DDR4 (MS-7D98)", "1.H0")
            .Single(x => x.Setting == "XMP");

        e.CeQuiManqueTout.Should().HaveCount(4);
        e.CeQuiManque.Should().Contain("recuperation").And.Contain("pas lisible");
    }
}
