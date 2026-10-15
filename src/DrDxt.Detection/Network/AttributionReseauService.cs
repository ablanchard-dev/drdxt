using DrDxt.Core;
using static DrDxt.Core.TexteFrancais;
using DrDxt.Measurement.Network;

namespace DrDxt.Detection.Network;

/// <summary>
/// D'ou vient l'instabilite du reseau, mesuree et pas seulement lue sur la carte : un joueur
/// competitif ressent le reseau avant tout le reste.
///
/// Ce service assemble la passerelle de la carte physique (jamais « la
/// premiere passerelle trouvee » : un VPN monte en a une aussi), deux fenetres de mesure, et
/// l'analyseur qui les compare sans seuil invente.
///
/// Ce qu'il rend quand il ne sait pas : `null`. Pas de carte, pas de passerelle, une mesure
/// refusee, un verdict indecidable ⇒ la ligne n'apparait pas. « Non mesure » ne se remplace pas
/// par « reseau sain » : c'est la meme regle que le regime soutenu du processeur.
///
/// Ordre d'appel : cette mesure doit passer avant le banc CPU du scan. Un banc qui sature huit
/// cœurs gonfle la gigue ICMP des deux fenetres, et c'est notre propre machine qui la fabrique.
/// Mesurer apres, ce serait attribuer au reseau une instabilite qu'on vient de creer.
/// </summary>
public sealed class AttributionReseauService
{
    /// <summary>Reference internet par defaut : un resolveur anycast qui repond a l'ICMP depuis
    /// n'importe quel operateur. Ce n'est pas le serveur de jeu : le caveat le dit dans la
    /// phrase, et aucun verdict ne pretend le contraire.</summary>
    public const string ReferenceInternetParDefaut = "1.1.1.1";

    // Deux secondes par fenetre : assez de sondes pour une gigue lisible, assez court pour ne pas
    // rallonger un scan qui charge deja le processeur pendant quarante secondes.
    private const int Sondes = 10;
    private static readonly TimeSpan Intervalle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(2);

    private readonly INetworkAdapterReader _cartes;
    private readonly INetworkLatencyMeter _compteur;
    private readonly string _reference;

    public AttributionReseauService(
        INetworkAdapterReader cartes, INetworkLatencyMeter compteur, string? reference = null)
    {
        _cartes = cartes;
        _compteur = compteur;
        _reference = string.IsNullOrWhiteSpace(reference) ? ReferenceInternetParDefaut : reference;
    }

    /// <summary>La phrase a afficher, ou <c>null</c> quand rien n'a pu etre attribue.</summary>
    public async Task<string?> LigneAsync(CancellationToken ct = default)
    {
        var passerelle = Passerelle();
        if (passerelle is null) return null;

        var premier = await Comparer(passerelle, ct).ConfigureAwait(false);
        if (premier is null) return null;
        if (premier.Verdict is not (NetworkPathVerdict.StartsAtFirstHop or NetworkPathVerdict.AppearsBeyondGateway))
            return Phrase(premier, passerelle);

        // Un verdict qui désigne (la box, l'opérateur) doit se reproduire. Mesuré sur la machine de reference avec
        // le chemin exact du produit : 30 sur 30 « rien à désigner » au repos, mais 3 sur 10 accusations sous
        // charge processeur (le PC occupé fabrique sa propre gigue). Une seconde comparaison doit dire la même
        // chose ; sinon la mesure n'accuse personne. Coût : quatre secondes, seulement quand il y a accusation.
        var second = await Comparer(passerelle, ct).ConfigureAwait(false);
        if (second is null) return null;
        return Phrase(second.Verdict == premier.Verdict ? second : second with { Verdict = NetworkPathVerdict.Indistinguishable },
            passerelle);
    }

    private async Task<NetworkPathReport?> Comparer(string passerelle, CancellationToken ct)
    {
        var saut = await Fenetre(passerelle, ct).ConfigureAwait(false);
        if (saut is null) return null;
        var internet = await Fenetre(_reference, ct).ConfigureAwait(false);
        return internet is null ? null : NetworkPathAnalyzer.Compare(saut, internet);
    }

    /// <summary>
    /// Le premier saut, et le refus qui fait tenir tout le raisonnement.
    ///
    /// L'analyseur n'a pas de seuil : il conclut parce que le premier saut est contenu dans le
    /// chemin complet. Cette inclusion est une hypothese sur le routage, pas une propriete de la
    /// mesure, et elle tombe des que la machine a plusieurs sorties. Ethernet et Wi-Fi connectes
    /// ensemble, un tunnel VPN monte : la passerelle qu'on mesurerait peut appartenir a un chemin
    /// que la reference internet n'emprunte pas. On comparerait alors deux trajets differents et
    /// on accuserait un lien qui ne porte rien.
    ///
    /// D'ou la regle : une seule passerelle publiee sur toute la machine, cartes virtuelles
    /// comprises. Une seule ⇒ tout ce qui sort passe par elle, l'inclusion est acquise.
    /// Plusieurs ⇒ rien ici ne dit laquelle porte la route, et le produit se tait.
    ///
    /// Et elle doit etre celle d'une carte physique : si l'unique passerelle appartient au seul
    /// tunnel VPN, mesurer « le lien local » repondrait sur le tunnel.
    /// </summary>
    public string? Passerelle()
    {
        var toutes = _cartes.ReadAllDefaultGateways();
        if (!toutes.IsOk || toutes.Value!.Count != 1) return null;
        var seule = toutes.Value![0];

        var cartes = _cartes.Read();
        if (!cartes.IsOk) return null;

        foreach (var carte in cartes.Value!)
        {
            var lu = _cartes.ReadDefaultGateway(carte);
            if (lu.IsOk && string.Equals(lu.Value, seule, StringComparison.OrdinalIgnoreCase))
                return seule;
        }
        return null;
    }

    private async Task<NetworkLatencyReport?> Fenetre(string cible, CancellationToken ct)
    {
        try
        {
            var r = await _compteur.MeasureAsync(cible, Sondes, Intervalle, Delai, ct).ConfigureAwait(false);
            return r.IsOk ? r.Value : null;
        }
        catch (Exception)
        {
            // Mesurer le reseau ne doit jamais etre la raison pour laquelle un scan s'arrete.
            return null;
        }
    }

    private static string? Phrase(NetworkPathReport rapport, string passerelle)
    {
        var saut = rapport.FirstHop.JitterMs;
        var net = rapport.Internet.JitterMs;

        // Le caveat voyage avec chaque phrase qui designe le premier saut, jamais en note de bas
        // de page : une box repond a l'ICMP avec son propre processeur, en basse priorite, donc un
        // premier saut instable peut venir de sa reponse et non de son acheminement.
        var caveat = Docteur.T("AR.Caveat");

        // « ICMP n'est pas le trafic du jeu » doit atteindre le joueur : le fait est dit dans chaque
        // verdict, a la suite de la mesure qu'il nuance.
        var pasLeJeu = Docteur.T("AR.PasLeJeu");

        return rapport.Verdict switch
        {
            NetworkPathVerdict.StartsAtFirstHop =>
                Docteur.T("AR.PremierSaut", passerelle, saut, net) + caveat + pasLeJeu,

            NetworkPathVerdict.AppearsBeyondGateway =>
                Docteur.T("AR.AuDela", passerelle, saut, net) + pasLeJeu,

            NetworkPathVerdict.Indistinguishable =>
                Docteur.T("AR.Indiscernable", saut, passerelle, net) + pasLeJeu,

            // Une fenetre sans gigue exploitable : la mesure a eu lieu, elle ne tranche pas. On ne
            // dit rien plutot que d'ecrire une phrase qui ressemblerait a un bilan de sante.
            _ => null,
        };
    }
}
