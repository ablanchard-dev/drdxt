using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Gpu.Intel;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Findings table of the "vicieux mais honnête" scan (§11): reveals the real gaps + an honest
/// estimated gain, never the fix. Every finding now comes from the shared <see cref="LeverCatalog"/>
/// — so a finding's <see cref="Finding.EstimatedGainPct"/> is exactly the penalty the score removed
/// (no more divergent number sets, audit C1 coupling). Gating (chassis / hardware / symptom) and
/// the Unknown→Info honesty live in the catalog. All keys are resx FR+EN; never a hardcoded string.
/// </summary>
public static class ScoringRules
{
    public static IReadOnlyList<Finding> Evaluate(HardwareInventory hw, SettingsState state)
    {
        var findings = new List<Finding>();

        foreach (var lever in LeverCatalog.Evaluate(hw, state))
            if (lever.Finding is { } f)
                findings.Add(f);

        // NS-4 (RAM reliability): a DISABLED page file is a real reliability hazard (at the commit
        // limit Windows hits "out of memory" → freeze/crash — Microsoft, https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file). Honest finding: Minor,
        // gain 0 (reliability, never a claimed FPS number), guidance only (there is
        // no auto-fix this pass; restoring "system-managed" is a WMI op outside the typed-op model).
        // Present/Unknown → nothing: we never invent a problem (§6/§11, audit C2).
        if (state.PagefileConfigured == TriState.Off)
            findings.Add(new Finding("Memory", Severity.Minor, "Finding.Memory.PagefileDisabled", 0.0));

        // NS-10 — la topologie memoire. Meme doctrine que l'Intel et le reseau plus bas : des
        // FINDINGS, aucun point retire, et surtout aucun chiffre de gain sur cette machine. Le cout
        // du simple canal est reel et gros (docs/RAM-etat-de-lart.md, R-01 : 5 a 34 % selon le
        // titre) mais il a ete mesure ailleurs, sur une autre configuration — l'ecrire ici comme un
        // gain promis serait exactement l'estimation que le produit a retiree de sa vitrine.
        // `ChannelTopologyUnknown` est Info et non Minor : ne pas savoir lire une carte est notre
        // lacune, pas un defaut du client, et on ne penalise personne pour un trou de notre
        // couverture.
        foreach (var g in MemoryTopologyAdvisor.Advise(hw.Memory, hw.Chassis, hw.MemorySlots))
        {
            // LM — la mesure ferme ce que la lecture des chaines croyait savoir. Quand le debit
            // releve depasse le plafond theorique d'UN canal, la topologie n'est plus une
            // deduction : elle est prouvee, quelle que soit la facon dont la carte nomme ses
            // emplacements. Seul >= 2 ferme : 1 veut dire « au moins un », donc rien de prouve.
            //
            // Les deux cles, et c'est la seconde qui compte le plus. `ChannelTopologyUnknown`
            // n'est qu'un aveu (Info, inoffensif). `SingleChannelPopulation` (Minor) dit au client
            // que ses deux barrettes sont sur le meme canal et l'envoie ouvrir son boitier --
            // depuis la meme heuristique de chaines, et dementi par la mesure. Ne faire taire que
            // le premier laissait le client demonter sa machine pour rien.
            //
            // `SingleChannelOneModule` n'est pas dans cette liste, et ne doit pas y entrer :
            // une seule barrette est le simple canal, aucun debit ne contredit ca.
            if (state.MemoryProvenChannels >= 2 &&
                g.MessageKey is "Finding.Memory.ChannelTopologyUnknown"
                             or "Finding.Memory.SingleChannelPopulation")
                continue;

            // La paire hors A2/B2 est Info : un risque de tenue en fréquence, pas un défaut prouvé (chantier B1).
            var severity = g.MessageKey is "Finding.Memory.ChannelTopologyUnknown" or "Finding.Memory.SlotPositionUnknown"
                or "Finding.Memory.SlotsNotA2B2" ? Severity.Info : Severity.Minor;
            findings.Add(new Finding("Memory", severity, g.MessageKey, null));
        }

        // NS-11 — la stabilite du profil memoire. Le catalogue dit `Optimal` des que XMP est
        // actif : vrai en performance, MUET sur la stabilite d'un overclock qu'on n'a pas teste.
        // Ce conseil n'accuse rien. `Kernel-Power 41` se logue a l'identique pour une
        // extinction au bouton ou a la prise -- ce projet a deja fait cette surinterpretation une
        // fois. Il pose deux faits lus et un UNKNOWN nomme, et propose la seule experience qui
        // tranche. Info, aucun chiffre : la classe est NON CHIFFRE.
        var stabilite = MemoryStabilityAdvisor.Advise(
            state.XmpExpo, state.UncleanShutdowns, hw.Chassis, DateTimeOffset.UtcNow);
        // UI1 (08/10/2026) : au-dessus du standard ET sous le kit, avec des arrêts : un seul constat dit les trois faits
        // (deux constats orange à la suite se lisaient comme contradictoires).
        var sousLeKit = hw.Chassis == ChassisType.Desktop
            && state.XmpExpo == TriState.On
            && hw.Memory.Count > 0
            && hw.Memory.All(m => m.ConfiguredMhz > 0 && m.RatedMhz > 0)
            && hw.Memory.Any(m => m.ConfiguredMhz < m.RatedMhz);
        foreach (var g in stabilite)
            findings.Add(new Finding("Memory", Severity.Info,
                sousLeKit && g.MessageKey == "Finding.Memory.XmpOnWithUncleanShutdowns" ? "Finding.Memory.XmpOnSousLeKitAvecArrets" : g.MessageKey,
                null));

        // 17/09/2026 — sous la vitesse du kit. Remarque du joueur : « a pas vu que mon vrai profil XMP pas
        // activé ». Sa mémoire (kit 4000) tourne à 3800 : au-dessus du standard, donc « XMP actif »
        // pour l'inférence, et le produit se taisait. Le fait lu est simple : la vitesse réglée est
        // sous celle du kit, le profil du kit n'est pas appliqué tel quel. Info, aucun chiffre, et le
        // texte ne pousse pas à remonter : une vitesse abaissée stabilise souvent une machine.
        // Seulement quand XMP est déduit actif (sinon c'est l'autre constat, sous le standard) et que
        // les deux vitesses de chaque module sont lues.
        // Avec des arrêts relevés, le constat fusionné ci-dessus le dit déjà.
        if (sousLeKit && stabilite.Count == 0)
            findings.Add(new Finding("Memory", Severity.Info, "Finding.Memory.SousLaVitesseDuKit", null));

        // 24/09/2026 — « performances maximales » au profil GLOBAL du pilote. Mesuré ce jour sur la
        // machine de référence, PC inactif : 2 490 MHz, mémoire vidéo au maximum, 55,6 W, raison
        // publiée « GPU inactif ». Ce réglage garde la carte au maximum hors jeu, toute la journée.
        // Le levier de DrDXT le pose au profil du JEU ; lu au global (scope null), c'est autre chose.
        var perfMaxGlobal = hw.Chassis == ChassisType.Desktop
            && state.NvidiaPreferMaxPerf == TriState.On
            && state.NvidiaDrsScope is null;
        if (perfMaxGlobal)
            findings.Add(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));

        // LM 07/09 — la consommation au repos. Trouve par l'audit : 55,9 W au repos sur une carte
        // plafonnee a 115 W, avec deux ecrans a 200 et 144 Hz. Aucun levier ne regardait ca, et
        // c'est le seul effet du produit qui joue 24 h sur 24 plutot qu'aux heures de jeu.
        // Info et aucun chiffre : les gains rapportes (R-02) viennent d'autres machines, et les
        // promettre ici serait l'estimation que ce produit a retiree de sa vitrine.
        // 24/09 : tant que le global est en performances maximales, la carte ne descend jamais,
        // quels que soient les écrans. Le conseil des fréquences ne peut alors pas être vérifié :
        // une cause à la fois, la prouvée d'abord.
        if (!perfMaxGlobal)
            foreach (var g in IdlePowerAdvisor.Advise(state.AttachedRefreshHz))
                findings.Add(new Finding("Display", Severity.Info, g.MessageKey, null));

        // NS-5 — Intel Arc. Sans ça le produit se TAIT sur une carte Arc : le tuning GPU est
        // refusé (GpuGating n'autorise que NVIDIA/AMD) et l'utilisateur ne l'apprend jamais.
        // Ce sont des FINDINGS, pas des leviers du registre de déduction : ils informent, ils
        // ne retirent aucun point — on ne pénalise pas quelqu'un pour une lacune de notre
        // couverture, et on n'invente pas de chiffre de gain (Intel donne un gain « variable
        // selon le titre » pour le pilote, intel.com « gaming technologies » ; et ReBAR « requis
        // pour une performance optimale », intel.com support 000092416, jusqu'à deux chiffres :
        // aucun des deux n'est un nombre qu'on peut promettre sur cette machine → null).
        foreach (var g in IntelGpuAdvisor.Advise(hw.Gpus))
        {
            var severity = g.Id == "gpu.intel.rebar_required" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Gpu", severity, g.MessageKey, null));
        }

        // NS-9 — réseau. Même doctrine que l'Intel ci-dessus : des FINDINGS, jamais des points
        // retirés, et aucun chiffre de gain (le guide Microsoft de réglage des cartes réseau : bénéfice
        // marginal, qui dépend du NIC — ce n'est pas promettable sur cette machine). `null` = jamais lu ⇒ silence :
        // « pas lu » ne doit rien conseiller.
        foreach (var g in NetworkAdvisor.Advise(state.NetworkAdapters ?? Array.Empty<NetworkAdapterState>()))
        {
            var severity = g.Id == "net.rss_off" ? Severity.Minor : Severity.Info;
            findings.Add(new Finding("Network", severity, g.MessageKey, null));
        }

        return findings;
    }
}
