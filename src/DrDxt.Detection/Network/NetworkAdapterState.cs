using DrDxt.Core;

namespace DrDxt.Detection.Network;

// -----------------------------------------------------------------------
// Lecture de la carte réseau + guidage. Aucune écriture, par décision, pas par paresse.
//
// Le guide Microsoft de réglage des cartes réseau est explicite (https://learn.microsoft.com/en-us/windows-server/networking/
// technologies/network-subsystem/net-sub-performance-tuning-nics) :
// interrupt moderation coupée « for network card drivers that require the lowest possible
// latency », mais au prix d'un usage CPU plus haut, avec un bénéfice qui dépend de la carte et
// reste marginal en jeu. Verdict du projet : pas chirurgical ⇒ guidage seul, jamais
// d'application automatique. Un levier automatique ici contredirait notre propre recherche :
// ce fichier lit l'état et conseille ; rien n'y peut écrire.
//
// RSS est le cas miroir : Microsoft dit qu'il doit rester actif (il répartit le traitement réseau
// entre les CPU). « RSS coupé » mérite donc d'être dit ; « RSS actif » se tait.
//
// Les mythes (Nagle/TcpAckFrequency, NetworkThrottlingIndex) ne sont pas ici : la liste noire
// les rend définitivement non livrables (net.nagle, net.tcpack, net.throttlingindex).
// -----------------------------------------------------------------------

/// <summary>Un conseil. Il porte sa source et c'est du guidage par construction : aucun chemin
/// d'application, aucune op, aucun id de levier. Le dire, jamais le faire en silence.
/// <paramref name="MessageKey"/> est la clé de ressource finale (convention
/// `Finding.&lt;Domaine&gt;&lt;Sujet&gt;`), pas un fragment à recomposer.</summary>
public sealed record NetworkGuidance(string Id, string Adapter, string MessageKey, string SourceUrl)
{
    /// <summary>Toujours vrai. Gardé explicite pour que l'intention survive à un refactoring et
    /// qu'un test vérifie que rien ici ne devient jamais applicable.</summary>
    public bool IsGuidanceOnly => true;
}

/// <summary>Règles pures : ni E/S, ni état. Un conseil ne vient que de ce qui a vraiment été
/// lu : un réglage Unknown donne le silence, jamais une recommandation.</summary>
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
            // RSS coupé est le seul cas net : Microsoft le veut actif, et l'activer ne coûte rien en
            // latence. Toujours du guidage (c'est une propriété de la carte, et on n'écrit pas les
            // propriétés de carte).
            if (a.Rss == TriState.Off)
                advice.Add(new NetworkGuidance(
                    "net.rss_off", a.Name, "Finding.Network.RssOff", NicTuningSource));

            // Interrupt moderation active est le défaut, et convient à la plupart. On la présente comme un
            // compromis à peser, pas comme un défaut à corriger : la couper baisse la latence sur les
            // cartes qui regroupent fort, et monte l'usage CPU. La clé de message doit porter les deux
            // sens, et l'instrument réseau peut mesurer si ça change quelque chose sur ce lien au lieu de deviner.
            if (a.InterruptModeration == TriState.On)
                advice.Add(new NetworkGuidance(
                    "net.interrupt_moderation_on", a.Name,
                    "Finding.Network.InterruptModerationTradeoff", NicTuningSource));

            // Les economies d'energie du lien (EEE et ses deux mots-cles voisins). Releve sur la
            // machine de reference : actives, alors qu'un scanner qui ne les lit pas dirait « rien a signaler ».
            //
            // Ce constat ne chiffre rien, et c'est un resultat de recherche, pas de la prudence.
            // `docs/LEVIERS-REPOS-ET-RESEAU.md` N-01 a N-03 : le reveil du lien se compte en
            // microsecondes, « borne et negligeable sauf usages tres sensibles au delai », et
            // aucune source ne chiffre l'effet en jeu. Notre plancher de bruit in-game est de
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
