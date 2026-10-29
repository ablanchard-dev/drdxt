using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Gpu.Intel;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Table des constats du scan : montre les vrais écarts, jamais la correction. Chaque constat vient du
/// <see cref="LeverCatalog"/> partagé : son <see cref="Finding.EstimatedGainPct"/> est la pénalité retirée par le score,
/// non affichée (FindingViewModel), elle sert au tri. Conditions et honnêteté Unknown→Info vivent dans le catalogue.
/// Toutes les clés sont des resx FR+EN.
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

        // Fichier d'échange désactivé : à la limite de validation, Windows manque de mémoire → gel / plantage (Microsoft,
        // https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file).
        // Minor, gain 0 (fiabilité), guidage seul : « géré par le système » est une op WMI hors du modèle d'ops typées.
        // Présent/Unknown → rien.
        if (state.PagefileConfigured == TriState.Off)
            findings.Add(new Finding("Memory", Severity.Minor, "Finding.Memory.PagefileDisabled", 0.0));

        // La mesure multi-fil qui reste sous le plafond d'un canal avec deux barrettes ou plus : un seul canal probable, quel
        // que soit le nom des emplacements.
        if (state.MemoryUnSeulCanalMesure)
            findings.Add(new Finding("Memory", Severity.Minor, "Finding.Memory.SingleChannelMeasured", null));

        // La topologie memoire : des constats, aucun point retire, aucun chiffre de gain. Le cout du simple canal est
        // reel (docs/RAM-etat-de-lart.md, R-01 : 5 a 34 % selon le titre) mais mesure sur une autre configuration.
        // `ChannelTopologyUnknown` est Info : ne pas savoir lire une carte est notre lacune, pas un defaut du client.
        foreach (var g in MemoryTopologyAdvisor.Advise(hw.Memory, hw.Chassis, hw.MemorySlots))
        {
            // Un debit mesure au-dessus du plafond d'un canal prouve la topologie, quelle que soit la facon dont la carte
            // nomme ses emplacements (1 veut dire « au moins un » : rien de prouve). Les deux cles se taisent, surtout
            // `SingleChannelPopulation` (Minor), qui enverrait le client ouvrir son boitier pour rien.
            // `SingleChannelOneModule` ne doit pas y entrer : une seule barrette est le simple canal.
            if (state.MemoryProvenChannels >= 2 &&
                g.MessageKey is "Finding.Memory.ChannelTopologyUnknown"
                             or "Finding.Memory.SingleChannelPopulation")
                continue;

            // La paire hors A2/B2 est Info : un risque de tenue en fréquence, pas un défaut prouvé.
            var severity = g.MessageKey is "Finding.Memory.ChannelTopologyUnknown" or "Finding.Memory.SlotPositionUnknown"
                or "Finding.Memory.SlotsNotA2B2" ? Severity.Info : Severity.Minor;
            findings.Add(new Finding("Memory", severity, g.MessageKey, null));
        }

        // La stabilite du profil memoire : `Optimal` au catalogue ne dit rien d'un overclock non teste. Le conseil
        // n'accuse rien (`Kernel-Power 41` se logue a l'identique pour une extinction au bouton ou a la prise) : deux
        // faits lus, un Unknown nomme, la seule experience qui tranche. Info, aucun chiffre.
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

        // Sous la vitesse du kit (kit 4000 à 3800 : « XMP actif » pour l'inférence) : le profil du kit n'est pas appliqué
        // tel quel. Info, aucun chiffre, et le texte ne pousse pas à remonter : une vitesse abaissée stabilise souvent
        // une machine. XMP déduit actif et les deux vitesses lues ; avec des arrêts, le constat fusionné le dit déjà.
        if (sousLeKit && stabilite.Count == 0)
            findings.Add(new Finding("Memory", Severity.Info, "Finding.Memory.SousLaVitesseDuKit", null));

        // « Performances maximales » au profil global du pilote garde la carte au maximum hors jeu. Mesuré sur la machine
        // de référence inactive : 2 490 MHz, mémoire vidéo au maximum, 55,6 W, raison « GPU inactif ». Le levier de
        // DrDXT le pose au profil du jeu ; lu au global (scope null), c'est autre chose.
        var perfMaxGlobal = hw.Chassis == ChassisType.Desktop
            && state.NvidiaPreferMaxPerf == TriState.On
            && state.NvidiaDrsScope is null;
        if (perfMaxGlobal)
            findings.Add(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));

        // La consommation au repos : 55,9 W relevés sur une carte plafonnée à 115 W avec deux écrans à 200 et 144 Hz ;
        // le seul effet du produit qui joue 24 h sur 24. Info, aucun chiffre (R-02 vient d'autres machines). Sous le
        // global en performances maximales, la carte ne descend jamais : une cause à la fois, la prouvée d'abord.
        if (!perfMaxGlobal)
            foreach (var g in IdlePowerAdvisor.Advise(state.AttachedRefreshHz))
                findings.Add(new Finding("Display", Severity.Info, g.MessageKey, null));

        // Intel Arc : le tuning GPU y est refusé (GpuGating n'autorise que NVIDIA/AMD), ces constats le disent. Aucun
        // point retiré, aucun chiffre : Intel annonce un gain pilote « variable selon le titre » (intel.com « gaming
        // technologies ») et ReBAR « requis pour une performance optimale » (intel.com support 000092416).
        foreach (var g in IntelGpuAdvisor.Advise(hw.Gpus))
        {
            var severity = g.Id == "gpu.intel.rebar_required" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Gpu", severity, g.MessageKey, null));
        }

        // Réseau : des constats, aucun point, aucun chiffre (guide Microsoft de réglage des cartes réseau : bénéfice
        // marginal, selon le NIC). `null` = jamais lu ⇒ silence.
        foreach (var g in NetworkAdvisor.Advise(state.NetworkAdapters ?? Array.Empty<NetworkAdapterState>()))
        {
            var severity = g.Id == "net.rss_off" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Network", severity, g.MessageKey, null));
        }

        return findings;
    }
}
