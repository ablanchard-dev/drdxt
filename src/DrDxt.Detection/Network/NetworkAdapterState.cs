using DrDxt.Core;

namespace DrDxt.Detection.Network;

// -----------------------------------------------------------------------
// NS-9 — network adapter read + GUIDANCE. No write, by decision, not by laziness.
//
// Microsoft's NIC tuning guide is explicit (https://learn.microsoft.com/en-us/windows-server/networking/
// technologies/network-subsystem/net-sub-performance-tuning-nics):
// interrupt moderation OFF "for network card drivers that require the lowest possible
// latency" — but at the cost of higher CPU usage, with a benefit that depends on the NIC
// and is marginal in games. The project's verdict: not surgical => guidance only, never
// auto-apply. Shipping an auto-tweak here would contradict our own research, so this file
// reads the state and advises; nothing in it can write.
//
// RSS is the mirror case: Microsoft says it must stay ON (it spreads network processing
// across CPUs). So "RSS is OFF" is worth telling the user; "RSS is ON" is silence.
//
// The myths (Nagle/TcpAckFrequency, NetworkThrottlingIndex) are not here: they are
// permanently unshippable via the §6 ban-list (net.nagle, net.tcpack, net.throttlingindex).
// -----------------------------------------------------------------------

/// <summary>A piece of advice. It carries its source and it is guidance BY CONSTRUCTION —
/// there is no apply path, no op, no tweak id. §22.3: say it, never do it silently.
/// <paramref name="MessageKey"/> est la clé de ressource FINALE (convention
/// `Finding.&lt;Domaine&gt;&lt;Sujet&gt;`), pas un fragment à recomposer.</summary>
public sealed record NetworkGuidance(string Id, string Adapter, string MessageKey, string SourceUrl)
{
    /// <summary>Always true. Kept explicit so the intent survives a future refactor and so a
    /// test can assert that nothing here ever becomes applicable.</summary>
    public bool IsGuidanceOnly => true;
}

/// <summary>Pure rules — no I/O, no state. Advice comes only from what was actually read:
/// an Unknown setting produces silence, never a recommendation.</summary>
public static class NetworkAdvisor
{
    /// <summary>La source qui etablit le phenomene sans le chiffrer : le lien s'endort et se
    /// reveille, et le reveil se compte en microsecondes. Elle voyage avec le conseil.</summary>
    private const string LinkPowerSavingSource = "https://arxiv.org/pdf/1707.02798";

    private const string NicTuningSource =
        "https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics";

    public static IReadOnlyList<NetworkGuidance> Advise(IReadOnlyList<NetworkAdapterState> adapters)
    {
        var advice = new List<NetworkGuidance>();
        foreach (var a in adapters)
        {
            // RSS OFF is the one clear-cut case: Microsoft wants it ON, and turning it on
            // costs nothing in latency. Still guidance (it is an adapter property, and we do
            // not write adapter properties).
            if (a.Rss == TriState.Off)
                advice.Add(new NetworkGuidance(
                    "net.rss_off", a.Name, "Finding.Network.RssOff", NicTuningSource));

            // Interrupt moderation ON is the DEFAULT and is correct for most people. It is
            // reported as a trade-off to consider, not as a fault to fix: turning it off
            // lowers latency on NICs that batch aggressively, and raises CPU usage. The
            // message key must carry that both-ways framing — and the NS-9 instrument can
            // now measure whether it changes anything on this link instead of guessing.
            if (a.InterruptModeration == TriState.On)
                advice.Add(new NetworkGuidance(
                    "net.interrupt_moderation_on", a.Name,
                    "Finding.Network.InterruptModerationTradeoff", NicTuningSource));

            // LM 07/09 — les economies d'energie du lien. Trouvees ACTIVES sur la machine de
            // reference par un audit, pendant que le scanner disait « rien a signaler » : EEE etait
            // lu et n'etait utilise nulle part (un champ mort), et les deux mots-cles voisins
            // n'etaient meme pas lus.
            //
            // Ce constat ne chiffre rien, et c'est un resultat de recherche, pas de la prudence.
            // `docs/LEVIERS-REPOS-ET-RESEAU.md` N-01 a N-03 : le reveil du lien se compte en
            // microsecondes, « borne et negligeable sauf usages tres sensibles au delai », et
            // Aucune source ne chiffre l'effet en jeu. Notre plancher de bruit in-game est de
            // 9,9 % : nous ne pourrons jamais le mesurer, donc jamais le promettre.
            //
            // Un seul constat meme si les deux mots-cles sont actifs : le client n'a pas a lire
            // deux fois la meme idee parce que sa carte l'expose sous deux noms.
            if (a.EnergyEfficientEthernet == TriState.On || a.LinkPowerSaving == TriState.On)
                advice.Add(new NetworkGuidance(
                    "net.link_power_saving_on", a.Name,
                    "Finding.Network.LinkPowerSavingOn", LinkPowerSavingSource));
        }
        return advice;
    }
}
