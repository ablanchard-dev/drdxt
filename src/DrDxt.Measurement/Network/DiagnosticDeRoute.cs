using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Ce que vendent ExitLag, GearUP, NoPing ou WTFast, c'est un réseau de relais qui
/// transporte la partie sur un chemin choisi pour la latence. Ils ne disent jamais quand ça ne sert à rien. Ce juge le dit,
/// à partir de la route mesurée vers le serveur de la partie, sans rien écrire et sans contacter personne d'autre.
///
/// Sources : Savage et al., SIGCOMM 1999 : un détour gagne plus de 25 ms aller-retour
/// sur environ 15 % des chemins, et réduit les pertes sur la plupart ; un relais ne touche pas au lien local (Wi-Fi, box,
/// envoi saturé).
/// </summary>
public enum VerdictRoute
{
    /// <summary>Pas assez de mesures vers le serveur : rien n'est affirmé.</summary>
    Inconnu = 0,

    /// <summary>L'instabilité commence sur le lien local : un relais passe par le même lien, il n'y fera rien.</summary>
    ProblemeLocal = 1,

    /// <summary>Ping bas, stable, sans perte : il n'y a plus rien qu'un détour puisse gagner.</summary>
    RouteDejaBonne = 2,

    /// <summary>Pertes ou à-coups au-delà de la box : c'est le cas où plusieurs chemins aident vraiment.</summary>
    RelaisPeutAider = 3,

    /// <summary>Route stable mais longue : un détour plus court existe peut-être, seul un essai comparé le dira.</summary>
    EssaiAComparer = 4,
}

/// <param name="Echantillons">Nombre de réponses reçues du serveur de la partie.</param>
/// <param name="PingMedianMs">Aller-retour moyen vers le serveur (le compteur de sondes rend la moyenne).</param>
/// <param name="PertePourcent">Part des sondes sans réponse.</param>
/// <param name="GigueMs">Écart moyen entre deux aller-retours successifs.</param>
/// <param name="Attribution">Où commence l'instabilité (<see cref="NetworkPathAnalyzer"/>).</param>
/// <summary>Un routeur du chemin vers le serveur : son rang (TTL), son adresse (<c>null</c> s'il n'a pas répondu) et son
/// aller-retour chronométré (Windows rend 0 ms pour un TTL expiré, relevé sur la machine le 03/10).</summary>
public sealed record SautDeRoute(int Ttl, System.Net.IPAddress? Adresse, double? Ms);

public sealed record MesureDeRoute(int Echantillons, double PingMedianMs, double PertePourcent, double GigueMs, NetworkPathVerdict Attribution);

public static class DiagnosticDeRoute
{
    /// <summary>En dessous, le verdict ne s'appuie sur rien de stable : une minute de sondes à une par seconde.</summary>
    public const int EchantillonsMinimum = 60;

    /// <summary>Le gain d'un détour mesuré par Savage (plus de 25 ms sur environ 15 % des chemins) ne peut pas exister
    /// sous ce ping : la route entière est plus courte que le gain.</summary>
    public const double PingSansGainPossibleMs = 30;

    /// <summary>Une perte de 1 % se voit déjà en partie (balles qui ne partent pas, téléportations).</summary>
    public const double PerteGenantePourcent = 1;

    /// <summary>Au-delà, l'aller-retour varie d'une image à 60 Hz sur deux.</summary>
    public const double GigueGenanteMs = 8;

    public static VerdictRoute Juger(MesureDeRoute m)
    {
        if (m.Echantillons < EchantillonsMinimum) return VerdictRoute.Inconnu;
        if (m.Attribution == NetworkPathVerdict.StartsAtFirstHop) return VerdictRoute.ProblemeLocal;

        var instable = m.PertePourcent >= PerteGenantePourcent || m.GigueMs > GigueGenanteMs;
        if (instable) return VerdictRoute.RelaisPeutAider;
        return m.PingMedianMs <= PingSansGainPossibleMs ? VerdictRoute.RouteDejaBonne : VerdictRoute.EssaiAComparer;
    }

    public const string Nom = "route vers le serveur de la partie";

    /// <summary>Le serveur n'a répondu à aucune sonde : beaucoup de serveurs de jeu ignorent le ping. Rien n'est affirmé.</summary>
    public static RepairFinding SansReponse(string serveur) => new(PertesInterface.Domaine, Nom, null, null, RepairStatus.Observed,
        Docteur.T("DR.SansReponse", serveur));

    /// <summary>Un serveur de Call of Duty ignore le ping. Les routeurs du chemin,
    /// eux, répondent (TTL expiré) : on juge la route jusqu'au dernier qui répond, au plus près du serveur. La boîte (premier
    /// saut) ne compte pas : une route mesurée jusqu'à elle ne dit rien d'Internet.</summary>
    public static SautDeRoute? DernierSautQuiRepond(IReadOnlyList<SautDeRoute> sauts) =>
        sauts.Where(s => s.Ttl > 1 && s.Adresse is not null).OrderBy(s => s.Ttl).LastOrDefault();

    /// <summary>La phrase du domaine Réseau pour un verdict. <paramref name="jusqua"/> : le routeur où la mesure s'arrête
    /// quand le serveur ignore le ping ; <c>null</c> quand le serveur lui-même a répondu.</summary>
    public static RepairFinding Constat(MesureDeRoute m, string serveur, SautDeRoute? jusqua = null)
    {
        var c = ConstatBrut(m, serveur);
        return jusqua is null ? c : c with
        {
            Explication = Docteur.T("DR.JusquAuRouteur", jusqua.Ttl, jusqua.Adresse, serveur)
                + char.ToUpper(c.Explication[0], Docteur.Langue) + c.Explication[1..],
        };
    }

    private static RepairFinding ConstatBrut(MesureDeRoute m, string serveur)
    {
        var lu = Docteur.T("DR.Lu", m.PingMedianMs, m.PertePourcent, m.GigueMs);
        return Juger(m) switch
        {
            VerdictRoute.RouteDejaBonne => new(PertesInterface.Domaine, Nom, lu, null, RepairStatus.ExpectedDefault,
                Docteur.T("DR.DejaBonne", serveur)),
            VerdictRoute.ProblemeLocal => new(PertesInterface.Domaine, Nom, lu, null, RepairStatus.Suspicious,
                Docteur.T("DR.Local")),
            VerdictRoute.RelaisPeutAider => new(PertesInterface.Domaine, Nom, lu, null, RepairStatus.Suspicious,
                Docteur.T("DR.Relais", serveur)),
            VerdictRoute.EssaiAComparer => new(PertesInterface.Domaine, Nom, lu, null, RepairStatus.Observed,
                Docteur.T("DR.Essai", m.PingMedianMs, serveur)),
            _ => new(PertesInterface.Domaine, Nom, null, null, RepairStatus.Observed,
                Docteur.T("DR.TropPeu", m.Echantillons)),
        };
    }
}
