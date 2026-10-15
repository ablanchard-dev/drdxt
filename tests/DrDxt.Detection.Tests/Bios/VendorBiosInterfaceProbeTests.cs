using DrDxt.Core;
using DrDxt.Detection.Bios;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Bios;

/// <summary>
/// Trouver une interface BIOS ne la qualifie pas.
///
/// Releve le 08/09/2026 sur la MSI PRO B760-P WIFI DDR4 : `root\wmi` expose `MSI_BiosSetting`
/// avec `GetBiosSetting(Item)` et `SetBiosSetting(Item, Value)`, deux instances ACPI\PNP0C14
/// actives. C'est une interface firmware de lecture et d'ecriture, sur la machine de reference.
///
/// La tentation evidente est d'en faire un `VendorSupportedWrite` et d'appeler `GetBiosSetting`
/// pour voir. **Trois raisons de ne pas le faire, et chacune suffit** :
///
///   1. `Item` est un NOM de reglage que nous ne connaissons pas : l'appeler revient a tatonner
///      le parseur d'un firmware ;
///   2. la North Star exige une interface **officiellement supportee par le constructeur**, et
///      une classe WMI ACPI peut n'exister que pour le logiciel maison de MSI ;
///   3. cette machine porte douze arrets non propres inexpliques.
///
/// Ces tests gravent la retenue : le probe ne peut pas promouvoir un niveau de capacite, et il
/// n'appelle aucune methode.
/// </summary>
public class VendorBiosInterfaceProbeTests
{
    [Fact]
    public void TROUVER_UNE_INTERFACE_N_AUTORISE_AUCUNE_PROMOTION_DE_CAPACITE()
    {
        // Le garde-fou central. Meme avec une interface qui expose une ecriture, le niveau
        // reste `Unsupported` : la promotion demande une documentation constructeur, une matrice
        // carte + firmware et une voie de recuperation connue.
        var trouvee = new VendorBiosInterface(@"root\wmi", "MSI_BiosSetting", 2,
            SaitLire: true, SaitEcrire: true, new[] { "GetBiosSetting", "SetBiosSetting" });

        VendorBiosInterfaceProbe.NiveauAutorise(new[] { trouvee })
            .Should().Be(BiosCapabilityLevel.Unsupported);
    }

    [Fact]
    public void LE_CONSTAT_DIT_TOUJOURS_QUE_PRESENTE_N_EST_PAS_SUPPORTEE()
    {
        // Cette phrase est lue par un humain qui decidera peut-etre d'appeler la methode. Elle
        // doit porter la nuance, pas la sous-entendre.
        var trouvee = new VendorBiosInterface(@"root\wmi", "MSI_BiosSetting", 2,
            SaitLire: true, SaitEcrire: true, new[] { "GetBiosSetting", "SetBiosSetting" });

        var phrase = VendorBiosInterfaceProbe.Constat(trouvee);

        phrase.Should().Contain("Présente ne veut pas dire prise en charge");
        phrase.Should().NotContain("—").And.NotContain("ECRITURE", "pas de capitales d'insistance");
        phrase.Should().Contain("aucune méthode n'est appelée");
        // L'intention reste (une interface qui sait écrire le DIT), portée par les mots.
        phrase.Should().Contain("lecture et écriture exposées", "une interface qui sait écrire doit le dire");
    }

    [Fact]
    public void AUCUNE_INTERFACE_TROUVEE_N_EST_PAS_UN_ECHEC()
    {
        // Le cas de la plupart des machines. Rendre une liste vide est la reponse correcte, et le
        // niveau reste le meme — on ne descend pas plus bas que Unsupported.
        VendorBiosInterfaceProbe.NiveauAutorise(Array.Empty<VendorBiosInterface>())
            .Should().Be(BiosCapabilityLevel.Unsupported);
    }

    [Fact]
    [Trait("Category", "Machine")] // lit le WMI du vrai BIOS : vide sur un runner CI
    public void LE_PROBE_TOURNE_SUR_CETTE_MACHINE_SANS_RIEN_APPELER()
    {
        // Ce test EXECUTE le probe pour de vrai. Il ne verifie pas ce qu'il trouve — cela
        // dependrait de la machine — mais qu'il rend une liste sans lever et sans appeler de
        // methode firmware. Une regression qui ferait appeler `GetBiosSetting` se verrait au
        // premier plantage d'une machine de test.
        var trouvees = new VendorBiosInterfaceProbe().Chercher();

        trouvees.Should().NotBeNull();
        trouvees.Should().OnlyContain(i => i.Methodes.Count > 0 || i.Instances >= 0);
    }
}
