using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Detection.Bios;

/// <summary>Ce qu'une interface BIOS constructeur expose, tel qu'on l'a LU.</summary>
/// <param name="Namespace">Le namespace WMI ou elle vit.</param>
/// <param name="Classe">La classe exposee.</param>
/// <param name="Instances">Combien d'instances repondent. 0 = la classe existe sans instance,
/// ce qui n'est pas la meme chose qu'absente.</param>
/// <param name="SaitLire">Une methode de lecture est exposee.</param>
/// <param name="SaitEcrire">Une methode d'ecriture est exposee. Savoir ecrire n'autorise
/// pas a ecrire.</param>
/// <param name="Methodes">Les methodes vues, pour le journal.</param>
public sealed record VendorBiosInterface(
    string Namespace,
    string Classe,
    int Instances,
    bool SaitLire,
    bool SaitEcrire,
    IReadOnlyList<string> Methodes);

/// <summary>
/// Une interface BIOS constructeur existe-t-elle sur cette machine ?
///
/// C'est la question qui decide du niveau de capacite de la North Star BIOS, et elle se mesure
/// en quelques secondes au lieu de se chercher dans une documentation. Decouverte le 08/09/2026
/// sur la MSI PRO B760-P WIFI DDR4 : `root\wmi` expose `MSI_BiosSetting` avec
/// `GetBiosSetting(Item)` et `SetBiosSetting(Item, Value)`, deux instances ACPI\PNP0C14 actives.
///
/// **ce probe n'appelle aucune methode.** Il lit la forme des classes, jamais leur contenu.
/// Trois raisons, et chacune suffit :
///
///   1. `GetBiosSetting` prend un NOM de reglage que nous ne connaissons pas. L'appeler
///      reviendrait a tatonner le parseur d'un firmware.
///   2. L'existence d'une classe WMI ACPI n'etablit pas que le constructeur la supporte
///      publiquement — elle peut n'exister que pour son propre logiciel. `VendorSupportedWrite`
///      exige une interface **officiellement supportee**, pas une interface presente.
///   3. Sur une machine qui porte des arrets non propres inexpliques, sonder une interface
///      firmware non documentee est exactement le mauvais geste.
///
/// Ce que ce probe etablit est donc : **une piste serieuse vers `VendorSupportedWrite`**, a
/// qualifier par la documentation constructeur avant tout appel. Rien de plus, et c'est deja
/// beaucoup — la plupart des machines n'en ont aucune.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VendorBiosInterfaceProbe
{
    /// <summary>Les interfaces connues des grands constructeurs. Cette liste dit ou REGARDER,
    /// jamais ce qui est supporte : trouver la classe est un fait, la qualifier en est un autre.</summary>
    public static readonly (string Namespace, string Classe, string Constructeur)[] Connues =
    {
        (@"root\wmi", "MSI_BiosSetting", "MSI"),
        (@"root\wmi", "MSI_BootOption", "MSI"),
        (@"root\dcim\sysman", "DCIM_BIOSEnumeration", "Dell"),
        (@"root\hp\instrumentedbios", "HP_BIOSSetting", "HP"),
        (@"root\wmi", "Lenovo_BiosSetting", "Lenovo"),
        (@"root\wmi", "AMIBIOSSetting", "AMI (generique)"),
    };

    /// <summary>Cherche les interfaces connues. Liste vide = aucune trouvee, ce qui est le cas le
    /// plus frequent et n'est pas un echec.</summary>
    public IReadOnlyList<VendorBiosInterface> Chercher()
    {
        var trouvees = new List<VendorBiosInterface>();
        foreach (var (espace, classe, _) in Connues)
        {
            var vue = Inspecter(espace, classe);
            if (vue is not null) trouvees.Add(vue);
        }
        return trouvees;
    }

    private static VendorBiosInterface? Inspecter(string espace, string classe)
    {
        try
        {
            // La DEFINITION de la classe, pas son contenu : on veut savoir ce qui existe.
            using var definition = new ManagementClass(new ManagementScope(espace),
                new ManagementPath(classe), null);
            definition.Get();

            var methodes = definition.Methods.Cast<MethodData>().Select(m => m.Name).ToList();
            if (methodes.Count == 0 && definition.Properties.Count == 0) return null;

            var instances = 0;
            try { instances = definition.GetInstances().Count; }
            catch { /* une classe sans instance lisible reste une classe presente */ }

            return new VendorBiosInterface(espace, classe, instances,
                SaitLire: methodes.Any(m => m.StartsWith("Get", StringComparison.OrdinalIgnoreCase)),
                SaitEcrire: methodes.Any(m => m.StartsWith("Set", StringComparison.OrdinalIgnoreCase)),
                methodes);
        }
        catch { return null; }   // absente : le cas normal sur la plupart des machines
    }

    /// <summary>
    /// Le niveau de capacite que cette decouverte autorise — et il s'arrete a `Unsupported`.
    ///
    /// Trouver une interface ne la qualifie pas. La promotion vers `VendorSupportedWrite`
    /// demande une documentation constructeur, une matrice carte + firmware, et une voie de
    /// recuperation connue. Cette methode existe pour que personne ne fasse le raccourci.
    /// </summary>
    public static BiosCapabilityLevel NiveauAutorise(IReadOnlyList<VendorBiosInterface> trouvees) =>
        BiosCapabilityLevel.Unsupported;

    /// <summary>La phrase a mettre dans le journal quand une interface est trouvee.</summary>
    public static string Constat(VendorBiosInterface i) =>
        // 25/09/2026 (rapport réel) : sans accents, un tiret cadratin et « ECRITURE » en capitales.
        $"{i.Classe} dans {i.Namespace}, {i.Instances} instance(s) : "
        + $"{(i.SaitLire ? "lecture" : "pas de lecture")} et "
        + $"{(i.SaitEcrire ? "écriture" : "pas d'écriture")} exposées "
        + $"({string.Join(", ", i.Methodes)}). Présente ne veut pas dire prise en charge : aucune méthode "
        + "n'est appelée tant que la documentation du constructeur n'a pas qualifié cette interface.";
}
