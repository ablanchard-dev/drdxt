using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Gpu.Intel;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Table des constats du scan « vicieux mais honnête » : montre les vrais écarts + un gain estimé
/// honnête, jamais la correction. Chaque constat vient du <see cref="LeverCatalog"/> partagé :
/// le <see cref="Finding.EstimatedGainPct"/> d'un constat est exactement la pénalité retirée par
/// le score (plus de chiffres divergents, couplage de l'audit C1). Les conditions (châssis /
/// matériel / symptôme) et l'honnêteté Unknown→Info vivent dans le catalogue. Toutes les clés
/// sont des resx FR+EN ; jamais de chaîne en dur.
/// </summary>
public static class ScoringRules
{
    /// <param name="measured">Le verdict mesuré de la session : un levier qu'il dément porte son motif dans son constat
    /// (<see cref="Finding.GainSuppressedReason"/>). Null = rien de mesuré, rien de retiré.</param>
    public static IReadOnlyList<Finding> Evaluate(HardwareInventory hw, SettingsState state, BottleneckVerdict? measured = null)
    {
        var findings = new List<Finding>();

        foreach (var lever in LeverCatalog.Evaluate(hw, state, measured))
            if (lever.Finding is { } f)
                findings.Add(f with { GainSuppressedReason = lever.GainSuppressedReason });

        // Fiabilité de la RAM : un fichier d'échange désactivé est un vrai risque (à la limite de
        // validation, Windows manque de mémoire → gel / plantage ; Microsoft, https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file). Constat honnête : Minor,
        // gain 0 (fiabilité, jamais un chiffre de FPS), guidage seul (pas de correction automatique
        // ici ; remettre « géré par le système » est une op WMI hors du modèle d'ops typées).
        // Présent/Unknown → rien : on n'invente jamais de problème.
        if (state.PagefileConfigured == TriState.Off)
            findings.Add(new Finding("Memory", Severity.Minor, "Finding.Memory.PagefileDisabled", 0.0));

        // La topologie memoire. Meme doctrine que l'Intel et le reseau plus bas : des
        // constats, aucun point retire, et surtout aucun chiffre de gain sur cette machine. Le cout
        // du simple canal est reel et gros (docs/RAM-etat-de-lart.md, R-01 : 5 a 34 % selon le
        // titre) mais il a ete mesure ailleurs, sur une autre configuration : l'ecrire ici comme un
        // gain promis serait exactement l'estimation que le produit a retiree de sa vitrine.
        // `ChannelTopologyUnknown` est Info et non Minor : ne pas savoir lire une carte est notre
        // lacune, pas un defaut du client, et on ne penalise personne pour un trou de notre
        // couverture.
        // La mesure multi-fil qui reste sous le plafond d'un canal avec deux barrettes ou plus : un seul canal probable, quel
        // que soit le nom des emplacements.
        if (state.MemoryUnSeulCanalMesure)
            findings.Add(new Finding("Memory", Severity.Minor, "Finding.Memory.SingleChannelMeasured", null));

        foreach (var g in MemoryTopologyAdvisor.Advise(hw.Memory, hw.Chassis, hw.MemorySlots))
        {
            // La mesure ferme ce que la lecture des chaines croyait savoir. Quand le debit
            // releve depasse le plafond theorique d'un seul canal, la topologie n'est plus une
            // deduction : elle est prouvee, quelle que soit la facon dont la carte nomme ses
            // emplacements. Seul >= 2 ferme : 1 veut dire « au moins un », donc rien de prouve.
            //
            // Les deux cles, et c'est la seconde qui compte le plus. `ChannelTopologyUnknown`
            // n'est qu'un aveu (Info, inoffensif). `SingleChannelPopulation` (Minor) dit au client
            // que ses deux barrettes sont sur le meme canal et l'envoie ouvrir son boitier,
            // depuis la meme heuristique de chaines, et dementi par la mesure. Ne faire taire que
            // le premier laisserait le client demonter sa machine pour rien.
            //
            // `SingleChannelOneModule` n'est pas dans cette liste, et ne doit pas y entrer :
            // une seule barrette est le simple canal, aucun debit ne contredit ca.
            if (state.MemoryProvenChannels >= 2 &&
                g.MessageKey is "Finding.Memory.ChannelTopologyUnknown"
                             or "Finding.Memory.SingleChannelPopulation")
                continue;

            // La paire hors A2/B2 est Info : un risque de tenue en fréquence, pas un défaut prouvé.
            var severity = g.MessageKey is "Finding.Memory.ChannelTopologyUnknown" or "Finding.Memory.SlotPositionUnknown"
                or "Finding.Memory.SlotsNotA2B2" ? Severity.Info : Severity.Minor;
            findings.Add(new Finding("Memory", severity, g.MessageKey, null));
        }

        // La stabilite du profil memoire. Le catalogue dit `Optimal` des que XMP est
        // actif : vrai en performance, muet sur la stabilite d'un overclock qu'on n'a pas teste.
        // Ce conseil n'accuse rien. `Kernel-Power 41` se logue a l'identique pour une
        // extinction au bouton ou a la prise : le surinterpreter est un piege connu. Il pose deux
        // faits lus et un Unknown nomme, et propose la seule experience qui tranche. Info, aucun
        // chiffre : la classe est non chiffree.
        var stabilite = MemoryStabilityAdvisor.Advise(
            state.XmpExpo, state.UncleanShutdowns, hw.Chassis, DateTimeOffset.UtcNow);
        // Au-dessus du standard et sous le kit, avec des arrêts : un seul constat dit les trois faits
        // (deux constats orange à la suite passeraient pour contradictoires).
        var sousLeKit = hw.Chassis == ChassisType.Desktop
            && state.XmpExpo == TriState.On
            && hw.Memory.Count > 0
            && hw.Memory.All(m => m.ConfiguredMhz > 0 && m.RatedMhz > 0)
            && hw.Memory.Any(m => m.ConfiguredMhz < m.RatedMhz);
        foreach (var g in stabilite)
            findings.Add(new Finding("Memory", Severity.Info,
                sousLeKit && g.MessageKey == "Finding.Memory.XmpOnWithUncleanShutdowns" ? "Finding.Memory.XmpOnSousLeKitAvecArrets" : g.MessageKey,
                null));

        // Sous la vitesse du kit. Remarque du joueur : « a pas vu que mon vrai profil XMP pas
        // activé ». Sa mémoire (kit 4000) tourne à 3800 : au-dessus du standard, donc « XMP actif »
        // pour l'inférence, et le produit se taisait. Le fait lu est simple : la vitesse réglée est
        // sous celle du kit, le profil du kit n'est pas appliqué tel quel. Info, aucun chiffre, et le
        // texte ne pousse pas à remonter : une vitesse abaissée stabilise souvent une machine.
        // Seulement quand XMP est déduit actif (sinon c'est l'autre constat, sous le standard) et que
        // les deux vitesses de chaque module sont lues.
        // Avec des arrêts relevés, le constat fusionné ci-dessus le dit déjà.
        if (sousLeKit && stabilite.Count == 0)
            findings.Add(new Finding("Memory", Severity.Info, "Finding.Memory.SousLaVitesseDuKit", null));

        // « performances maximales » au profil global du pilote. Mesuré sur la
        // machine de référence, PC inactif : 2 490 MHz, mémoire vidéo au maximum, 55,6 W, raison
        // publiée « GPU inactif ». Ce réglage garde la carte au maximum hors jeu, toute la journée.
        // Le levier de DrDXT le pose au profil du jeu ; lu au global (scope null), c'est autre chose.
        var perfMaxGlobal = hw.Chassis == ChassisType.Desktop
            && state.NvidiaPreferMaxPerf == TriState.On
            && state.NvidiaDrsScope is null;
        if (perfMaxGlobal)
            findings.Add(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));

        // La consommation au repos. Trouve par l'audit : 55,9 W au repos sur une carte
        // plafonnee a 115 W, avec deux ecrans a 200 et 144 Hz. Aucun levier ne regardait ca, et
        // c'est le seul effet du produit qui joue 24 h sur 24 plutot qu'aux heures de jeu.
        // Info et aucun chiffre : les gains rapportes (R-02) viennent d'autres machines, et les
        // promettre ici serait l'estimation que ce produit a retiree de sa vitrine.
        // Tant que le global est en performances maximales, la carte ne descend jamais,
        // quels que soient les écrans. Le conseil des fréquences ne peut alors pas être vérifié :
        // une cause à la fois, la prouvée d'abord.
        if (!perfMaxGlobal)
            foreach (var g in IdlePowerAdvisor.Advise(state.AttachedRefreshHz))
                findings.Add(new Finding("Display", Severity.Info, g.MessageKey, null));

        // Intel Arc. Sans ça le produit se tait sur une carte Arc : le tuning GPU est
        // refusé (GpuGating n'autorise que NVIDIA/AMD) et l'utilisateur ne l'apprend jamais.
        // Ce sont des constats, pas des leviers du registre de déduction : ils informent, ils
        // ne retirent aucun point : on ne pénalise pas quelqu'un pour une lacune de notre
        // couverture, et on n'invente pas de chiffre de gain (Intel donne un gain « variable
        // selon le titre » pour le pilote, intel.com « gaming technologies » ; et ReBAR « requis
        // pour une performance optimale », intel.com support 000092416, jusqu'à deux chiffres :
        // aucun des deux n'est un nombre qu'on peut promettre sur cette machine → null).
        foreach (var g in IntelGpuAdvisor.Advise(hw.Gpus))
        {
            var severity = g.Id == "gpu.intel.rebar_required" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Gpu", severity, g.MessageKey, null));
        }

        // Réseau. Même doctrine que l'Intel ci-dessus : des constats, jamais des points
        // retirés, et aucun chiffre de gain (le guide Microsoft de réglage des cartes réseau : bénéfice
        // marginal, qui dépend du NIC, pas promettable sur cette machine). `null` = jamais lu ⇒ silence :
        // « pas lu » ne doit rien conseiller.
        foreach (var g in NetworkAdvisor.Advise(state.NetworkAdapters ?? Array.Empty<NetworkAdapterState>()))
        {
            var severity = g.Id == "net.rss_off" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Network", severity, g.MessageKey, null));
        }

        return findings;
    }
}
