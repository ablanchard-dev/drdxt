namespace DrDxt.Measurement.Network;

// -----------------------------------------------------------------------
// Attribuer l'instabilité : le lien local, ou au-delà ?
//
// Mesurer une seule cible dit qu'il y a du jitter, jamais d'où il vient. Deux fenêtres (la
// passerelle, premier saut, et une référence internet) permettent de trancher une
// partie de la question sans inventer de seuil.
//
// Le raisonnement, et il tient sans constante arbitraire : le premier saut est contenu
// dans le chemin complet. Tout ce qui perturbe le lien local perturbe donc aussi la
// mesure internet. Si le jitter du premier saut est déjà égal ou supérieur à celui du
// chemin complet, l'instabilité est présente avant même de sortir de la maison : le
// trajet au-delà n'ajoute rien de mesurable. C'est un ordre de grandeur, pas un seuil :
// il n'y a aucun nombre à justifier.
//
// Ce que ça ne prouve pas, et qui doit voyager avec le verdict : une box répond à l'ICMP
// avec son propre processeur, en basse priorité. Un premier saut instable peut donc venir
// de sa réponse et non de son acheminement. Le verdict est une piste à confirmer (par
// exemple en changeant de câble ou de port), jamais une conclusion.
// -----------------------------------------------------------------------

public enum NetworkPathVerdict
{
    /// <summary>Une des deux fenêtres n'a pas de jitter exploitable : on ne tranche pas.</summary>
    Unknown = 0,

    /// <summary>Le jitter est déjà présent au premier saut, à hauteur au moins égale à celui du
    /// chemin complet. L'instabilité commence sur le lien local (ou dans la réponse de la box).</summary>
    StartsAtFirstHop = 1,

    /// <summary>Le chemin complet est nettement plus instable que le premier saut : ce qui
    /// s'ajoute vient d'au-delà de la passerelle (opérateur, transit, destination).</summary>
    AppearsBeyondGateway = 2,

    /// <summary>Les deux fenêtres se valent à la résolution de l'instrument près : la mesure a
    /// eu lieu, et elle ne désigne personne. C'est le verdict normal d'un lien sain : sans lui,
    /// tout réseau mesurable hériterait d'un coupable.</summary>
    Indistinguishable = 3,
}

public sealed record NetworkPathReport(
    NetworkLatencyReport FirstHop,
    NetworkLatencyReport Internet,
    NetworkPathVerdict Verdict)
{
    /// <summary>Le caveat qui doit accompagner le verdict : une box répond à l'ICMP avec son
    /// propre CPU, en basse priorité. Un premier saut instable est une piste, pas une preuve.</summary>
    public string CaveatKey => "measure.network.first_hop_caveat";
}

public static class NetworkPathAnalyzer
{
    /// <summary>Compare les deux fenêtres. Aucune constante : le verdict ne repose que sur
    /// l'inclusion du premier saut dans le chemin complet.</summary>
    /// <summary>
    /// La plus petite différence de jitter que l'instrument sait distinguer. Ce n'est pas un
    /// seuil de confort qu'on aurait choisi : <c>Ping</c> ne rend que des millisecondes entières
    /// (voir <c>SystemPingProbe</c>, qui doit déjà représenter une réponse sous la milliseconde),
    /// donc en dessous d'une milliseconde d'écart il n'y a rien à départager. Comparer plus fin
    /// que ce que la sonde mesure, c'est lire du bruit.
    /// </summary>
    private const double ProbeResolutionMs = 1.0;

    public static NetworkPathReport Compare(NetworkLatencyReport firstHop, NetworkLatencyReport internet)
    {
        var verdict = (firstHop.JitterMs, internet.JitterMs) switch
        {
            // Sans jitter des deux côtés, il n'y a rien à comparer. On ne devine pas.
            (null, _) or (_, null) => NetworkPathVerdict.Unknown,
            // Mesuré des deux côtés, mais l'écart est sous la résolution de la sonde : on a
            // regardé, et il n'y a rien à dire. Nommer un coupable ici fabriquerait un défaut
            // à partir du bruit : quatre passages sur un même lien sain feraient basculer le
            // verdict d'un bord à l'autre, et un écart de 0,09 ms accuserait le lien local.
            var (hop, net) when Math.Abs(hop.Value - net.Value) < ProbeResolutionMs
                => NetworkPathVerdict.Indistinguishable,
            var (hop, net) => hop > net
                ? NetworkPathVerdict.StartsAtFirstHop
                : NetworkPathVerdict.AppearsBeyondGateway,
        };
        return new NetworkPathReport(firstHop, internet, verdict);
    }
}
