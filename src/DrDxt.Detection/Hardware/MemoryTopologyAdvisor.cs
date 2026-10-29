using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un conseil sur la disposition physique de la memoire. Meme forme que
/// <c>IntelGpuGuidance</c> et <c>NetworkGuidance</c> : guidance par construction, aucun chemin
/// d'application n'existe dans ce namespace. <paramref name="MessageKey"/> est la cle de ressource
/// finale, jamais un fragment recompose.</summary>
public sealed record MemoryTopologyGuidance(string Id, string Subject, string MessageKey, string SourceUrl)
{
    public bool IsGuidanceOnly => true;
}

/// <summary>
/// Le canal, le seul defaut memoire prouvable sans mesure : `MemoryDetector` lit `DeviceLocator`, et deux controleurs
/// distincts prouvent le dual channel. Le plus courant d'une machine assemblee vite : une barrette, ou deux du meme
/// cote. Cout (`docs/RAM-ETAT-DE-LART.md`, R-01) : 5 a 34 % selon le titre, 1 % low pris entre 20 et 33 %, au-dessus du
/// plancher de bruit de 9,9 % du banc.
/// Il ne devine pas : deux conventions existent (`Controller0-DIMMA2`, le controleur nomme ; `DIMMA1`, la seule lettre
/// de canal), et une carte peut n'en suivre aucune ; ni controleur ni lettre ⇒ `Unknown`, jamais simple canal par
/// defaut (un faux positif ferait demonter une machine bien montee). Rien sur le rang (`Ranks`) : le dual rank abaisse
/// le plafond de frequence (R-04), mais aucune frequence n'est conseillee.
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

    // `DIMMA2`, `DIMM_B1` : le chiffre qui suit la lettre de canal est la position.
    private static readonly Regex Position =
        new(@"DIMM[\s_-]*[A-Z]([0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="emplacements">Les emplacements de la carte (SMBIOS, 0 = non lu).</param>
    public static IReadOnlyList<MemoryTopologyGuidance> Advise(
        IReadOnlyList<MemoryModule> modules, ChassisType chassis, int emplacements = 0)
    {
        // Le chassis d'abord : un portable rapporte souvent une seule entree pour une memoire soudee multi-canal, et
        // personne n'ajoute une barrette a un portable ou a une console. Rien sur la topologie hors d'un fixe, comme pour
        // les autres constats memoire.
        if (chassis != ChassisType.Desktop) return Array.Empty<MemoryTopologyGuidance>();

        if (modules.Count == 0) return Array.Empty<MemoryTopologyGuidance>();

        // Une seule barrette est le simple canal, sans chaine a comprendre : le cas le plus frequent, et certain.
        if (modules.Count == 1)
            return One("memory.single_channel_one_module", "Finding.Memory.SingleChannelOneModule");

        var read = modules.Select(Discriminators).ToList();

        // Un seul emplacement illisible rend le verdict illisible : pas de conclusion sur une partie des barrettes.
        if (read.Any(d => d.Controller is null && d.Channel is null))
            return One("memory.channel_topology_unknown", "Finding.Memory.ChannelTopologyUnknown");

        // Deux canaux sont prouves des qu'un seul discriminant lu differe : certaines cartes nomment `Controller0` partout
        // et ne distinguent que par la lettre (`Controller0-DIMMA2` / `Controller0-DIMMB2`).
        if (Differs(read.Select(d => d.Controller)) || Differs(read.Select(d => d.Channel)))
            return PaireConseillee(modules, emplacements);

        return One("memory.single_channel_population", "Finding.Memory.SingleChannelPopulation");
    }

    // Double canal prouvé, reste la paire. Sur une carte à quatre emplacements, les
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
