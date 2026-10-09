using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un conseil sur la disposition physique de la memoire. Meme forme que
/// <c>IntelGpuGuidance</c> et <c>NetworkGuidance</c> : guidance par CONSTRUCTION, aucun chemin
/// d'application n'existe dans ce namespace. <paramref name="MessageKey"/> est la cle de ressource
/// FINALE, jamais un fragment recompose.</summary>
public sealed record MemoryTopologyGuidance(string Id, string Subject, string MessageKey, string SourceUrl)
{
    public bool IsGuidanceOnly => true;
}

/// <summary>
/// Le canal, le seul defaut memoire qu'on peut prouver sans rien mesurer.
///
/// `MemoryDetector` lit `DeviceLocator` depuis le 26/08 et le contrat dit noir sur blanc que deux
/// controleurs distincts PROUVENT le dual channel. Personne ne l'a jamais lu : le produit avait la
/// donnee dans la main et se taisait sur le defaut memoire le plus courant d'une machine assemblee
/// vite -- une seule barrette, ou deux barrettes du meme cote de la carte.
///
/// Ce que ca coute est dans `docs/RAM-ETAT-DE-LART.md` (R-01) : 5 a 34 % selon le titre, et les
/// 1 % low pris entre 20 et 33 %. C'est au-dessus du plancher de bruit de 9,9 % du banc, ce qu'on
/// ne peut dire d'a peu pres aucun reglage logiciel.
///
/// ══ ce qu'il refuse de faire ══
/// Il ne devine pas. Deux conventions d'emplacement existent sur les cartes grand public --
/// `Controller0-DIMMA2` (le controleur est nomme) et `DIMMA1` (seule la lettre de canal l'est) --
/// et une carte peut n'en suivre aucune. Quand la chaine ne rend ni controleur ni lettre, le
/// verdict est `Unknown` et le produit le DIT ; il ne conclut jamais au simple canal par defaut.
/// Un faux positif ici enverrait un client demonter une machine correctement montee.
///
/// Il ne dit rien non plus du rang (`Ranks`), et c'est volontaire : le dual rank abaisse le
/// plafond de frequence atteignable (R-04), mais le produit ne conseille aucune frequence, donc
/// il n'a rien a en faire. Une donnee lue n'oblige pas a un conseil.
/// </summary>
public static class MemoryTopologyAdvisor
{
    // La source qui chiffre le cout du simple canal. Elle voyage avec le conseil, pas a cote.
    public const string ChannelSourceUrl =
        "https://www.noobfeed.com/hardware/single-vs-dual-ddr5-gaming-performance";

    // `Controller0-DIMMA2` : le controleur est nomme, c'est la preuve la plus directe.
    private static readonly Regex ControllerToken =
        new(@"Controller\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // `DIMMA2`, `DIMM_A1`, `ChannelA-DIMM0` : la lettre qui suit designe le canal.
    private static readonly Regex ChannelLetter =
        new(@"(?:DIMM|Channel)[\s_-]*([A-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 06/10/2026 (chantier B1) : `DIMMA2`, `DIMM_B1` : le chiffre qui suit la lettre de canal est la position.
    private static readonly Regex Position =
        new(@"DIMM[\s_-]*[A-Z]([0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="emplacements">Les emplacements de la carte (SMBIOS, 0 = non lu).</param>
    public static IReadOnlyList<MemoryTopologyGuidance> Advise(
        IReadOnlyList<MemoryModule> modules, ChassisType chassis, int emplacements = 0)
    {
        // Le chassis d'abord, et ce n'est pas une formalite. Une machine portable rapporte
        // souvent une seule entree pour de la memoire soudee qui tourne pourtant sur plusieurs
        // canaux : conclure au simple canal y serait faux. Et meme quand c'est vrai, personne
        // n'ajoute une barrette a un portable ou a une console. Le produit tait donc la
        // topologie ailleurs que sur un fixe -- meme regle que tous les autres constats memoire.
        if (chassis != ChassisType.Desktop) return Array.Empty<MemoryTopologyGuidance>();

        if (modules.Count == 0) return Array.Empty<MemoryTopologyGuidance>();

        // Une seule barrette est le simple canal, et aucune chaine n'a besoin d'etre comprise
        // pour le dire. C'est le cas le plus frequent et le seul qui soit certain.
        if (modules.Count == 1)
            return One("memory.single_channel_one_module", "Finding.Memory.SingleChannelOneModule");

        var read = modules.Select(Discriminators).ToList();

        // Un seul emplacement illisible suffit a rendre le verdict illisible. Conclure sur les
        // autres, ce serait appliquer la regle a une partie de ce qu'elle couvre -- le defaut
        // que cette boucle a trouve cinq fois ailleurs.
        if (read.Any(d => d.Controller is null && d.Channel is null))
            return One("memory.channel_topology_unknown", "Finding.Memory.ChannelTopologyUnknown");

        // Deux canaux sont prouves des qu'UN discriminant lu differe. Le piege que ca evite :
        // certaines cartes nomment `Controller0` pour tous les emplacements et ne distinguent
        // que par la lettre (`Controller0-DIMMA2` / `Controller0-DIMMB2`). Trancher sur le seul
        // controleur y accuserait de simple canal une machine parfaitement montee.
        if (Differs(read.Select(d => d.Controller)) || Differs(read.Select(d => d.Channel)))
            return PaireConseillee(modules, emplacements);

        return One("memory.single_channel_population", "Finding.Memory.SingleChannelPopulation");
    }

    // 06/10/2026 (chantier B1) : double canal prouvé, reste la paire. Sur une carte à quatre emplacements, les
    // manuels MSI et ASUS demandent A2 et B2 pour deux barrettes (le bout de la piste, en chaîne) ; ailleurs, la mémoire
    // tient moins bien sa fréquence. Le coût en images n'est pas mesuré : aucun chiffre, et le manuel de la carte reste
    // juge (une carte en T fait exception). Sur deux emplacements, ou sans le nombre d'emplacements, rien n'est dit.
    private static IReadOnlyList<MemoryTopologyGuidance> PaireConseillee(IReadOnlyList<MemoryModule> modules, int emplacements)
    {
        if (modules.Count != 2 || emplacements != 4) return Array.Empty<MemoryTopologyGuidance>();
        var positions = modules.Select(m => Position.Match(m.DeviceLocator ?? "")).ToList();
        if (positions.Any(p => !p.Success)) return One("memory.slot_position_unknown", "Finding.Memory.SlotPositionUnknown");
        return positions.All(p => p.Groups[1].Value == "2")
            ? Array.Empty<MemoryTopologyGuidance>()
            : One("memory.slots_not_a2b2", "Finding.Memory.SlotsNotA2B2");
    }

    private static bool Differs(IEnumerable<string?> values)
        => values.Where(v => v is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    private static (string? Controller, string? Channel) Discriminators(MemoryModule m)
    {
        if (string.IsNullOrWhiteSpace(m.DeviceLocator)) return (null, null);

        var controller = ControllerToken.Match(m.DeviceLocator);
        var channel = ChannelLetter.Match(m.DeviceLocator);
        return (controller.Success ? controller.Groups[1].Value : null,
                channel.Success ? channel.Groups[1].Value.ToUpperInvariant() : null);
    }

    private static IReadOnlyList<MemoryTopologyGuidance> One(string id, string messageKey)
        => new[] { new MemoryTopologyGuidance(id, "Memory", messageKey, ChannelSourceUrl) };
}
