using DrDxt.Core;
using DrDxt.Measurement.Network;

namespace DrDxt.Detection.Network;

/// <summary>
/// D'ou vient l'instabilite du reseau, mesuree et pas seulement lue sur la carte. Assemble la passerelle de la carte
/// physique (jamais « la premiere trouvee » : un VPN en a une aussi), deux fenetres de mesure, et l'analyseur qui les
/// compare sans seuil invente. Sans carte, passerelle, mesure ou verdict decidable : `null`, la ligne n'apparait pas
/// (« non mesure » n'est pas « reseau sain »).
/// A appeler avant le banc CPU du scan : huit cœurs satures gonflent la gigue ICMP, et on accuserait le reseau d'une
/// instabilite qu'on vient de creer.
/// </summary>
public sealed class AttributionReseauService
{
    /// <summary>Reference internet par defaut : un resolveur anycast qui repond a l'ICMP depuis n'importe quel operateur.
    /// Pas le serveur de jeu, et la phrase le dit.</summary>
    public const string ReferenceInternetParDefaut = "1.1.1.1";

    // Deux secondes par fenetre : une gigue lisible sans rallonger un scan qui charge deja le processeur 40 s.
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
        // La passerelle se lit par WMI (centaines de ms) : hors du fil appelant, celui de l'écran pendant le scan.
        var passerelle = await Task.Run(Passerelle, ct).ConfigureAwait(false);
        if (passerelle is null) return null;

        var premier = await Comparer(passerelle, ct).ConfigureAwait(false);
        if (premier is null) return null;
        if (premier.Verdict is not (NetworkPathVerdict.StartsAtFirstHop or NetworkPathVerdict.AppearsBeyondGateway))
            return Phrase(premier, passerelle);

        // Un verdict qui désigne (la box, l'opérateur) doit se reproduire : sur la machine de reference, par le chemin du
        // produit, 30 sur 30 « rien à désigner » au repos mais 3 accusations sur 10 sous charge processeur. Une seconde
        // comparaison doit dire la même chose, sinon personne n'est accusé (quatre secondes, seulement en cas d'accusation).
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
    /// Le premier saut. L'analyseur conclut sans seuil parce que le premier saut est contenu dans le chemin complet : une
    /// hypothese de routage, fausse des que la machine a plusieurs sorties (Ethernet et Wi-Fi ensemble, un tunnel VPN), ou
    /// on accuserait un lien qui ne porte rien. D'ou : une seule passerelle publiee sur toute la machine, cartes
    /// virtuelles comprises (plusieurs ⇒ on se tait), et celle d'une carte physique (sinon on mesurerait le tunnel).
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

        // Le caveat accompagne chaque phrase qui designe le premier saut : une box repond a l'ICMP avec son processeur, en
        // basse priorite, et l'instabilite peut venir de sa reponse, pas de son acheminement.
        var caveat = Docteur.T("AR.Caveat");

        // « ICMP n'est pas le trafic du jeu » est dit dans chaque verdict, a la suite de la mesure qu'il nuance.
        var pasLeJeu = Docteur.T("AR.PasLeJeu");

        return rapport.Verdict switch
        {
            NetworkPathVerdict.StartsAtFirstHop =>
                Docteur.T("AR.PremierSaut", passerelle, saut, net) + caveat + pasLeJeu,

            NetworkPathVerdict.AppearsBeyondGateway =>
                Docteur.T("AR.AuDela", passerelle, saut, net) + pasLeJeu,

            NetworkPathVerdict.Indistinguishable =>
                Docteur.T("AR.Indiscernable", saut, passerelle, net) + pasLeJeu,

            // Sans gigue exploitable, la mesure ne tranche pas : rien plutot qu'une phrase qui ressemblerait a un bilan.
            _ => null,
        };
    }
}
