using System.Net.NetworkInformation;
using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Les pertes a l'interface pendant une fenetre, classees comme dans `tools/repairaudit` : dans l'App, la fenetre de
/// mesure en jeu du scan (la carte en partie, pas au repos). La carte (cable, port, pilote qui jette des trames), pas la
/// route : un lien peut pinguer parfaitement en perdant des paquets a l'interface.
/// </summary>
public static class PertesInterface
{
    public const string Domaine = "RESEAU";
    public const string Nom = "pertes a l'interface";

    /// <summary>L'identifiant systeme d'une carte, a partir de son nom. null = introuvable.</summary>
    public static string? IdDeLaCarte(string nomCarte)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, nomCarte, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch { return null; }
    }

    /// <param name="attendre">Pour les tests ; par defaut, la fenetre s'ecoule vraiment.</param>
    public static RepairFinding Mesurer(INicCounterSource source, string interfaceId, TimeSpan fenetre,
        Action<TimeSpan>? attendre = null)
    {
        var avant = source.Read(interfaceId);
        if (!avant.IsOk) return Inconnu(avant.Error?.Message ?? Docteur.T("PI.Illisibles"));

        if (attendre is null) Thread.Sleep(fenetre); else attendre(fenetre);

        var apres = source.Read(interfaceId);
        if (!apres.IsOk) return Inconnu(apres.Error?.Message ?? Docteur.T("PI.Illisibles"));

        return Classer(NicErrorCounters.Diff(avant.Value!, apres.Value!), fenetre);
    }

    /// <summary>Une difference de compteurs, un constat. Un compteur qui a recule n'est jamais « rien a
    /// signaler » : la carte a ete reinitialisee et l'information est perdue.</summary>
    public static RepairFinding Classer(NicErrorDelta delta, TimeSpan fenetre)
    {
        var secondes = Math.Round(fenetre.TotalSeconds);

        if (delta.CounterReset)
            return Inconnu(Docteur.T("PI.Recule"));

        var taux = delta.ReceivedDiscardPercent;
        // Chaque sens se dit s'il a bougé : une valeur « 0 / 0 en réception » sous un statut suspect cacherait l'émission.
        var sens = new List<string>();
        if (delta.ReceivedDiscarded > 0 || delta.ReceivedErrors > 0)
            sens.Add(Docteur.T("PI.Pertes", delta.ReceivedDiscarded, delta.ReceivedErrors));
        if (delta.SentDiscarded > 0 || delta.SentErrors > 0)
            sens.Add(Docteur.T("PI.PertesEmission", delta.SentDiscarded, delta.SentErrors));
        if (delta.IsClean)
            return new RepairFinding(Domaine, Nom, Docteur.T("PI.Aucune"), Docteur.T("PI.Aucune"), RepairStatus.ExpectedDefault,
                Docteur.T("PI.Propre", secondes, delta.ReceivedPackets, delta.SentPackets));
        // Windows ignore aussi, normalement, les trames d'un protocole qu'il ne connaît pas : quelques-unes ne disent rien de la
        // carte. Une erreur (CRC, matériel) compte dès la première ; des trames ignorées, à partir d'un seuil.
        var significatif = delta.ReceivedErrors > 0 || delta.SentErrors > 0
            || IgnoresSignificatifs(delta.ReceivedDiscarded, delta.ReceivedPackets)
            || IgnoresSignificatifs(delta.SentDiscarded, delta.SentPackets);
        return !significatif
            ? new RepairFinding(Domaine, Nom, string.Join(" ; ", sens), Docteur.T("PI.Aucune"), RepairStatus.Observed,
                Docteur.T("PI.PeuDIgnores", secondes, IgnoresMinimum, IgnoresMinimumPourcent))
            : new RepairFinding(Domaine, Nom,
                string.Join(" ; ", sens),
                Docteur.T("PI.Aucune"), RepairStatus.Suspicious,
                Docteur.T("PI.Sur", delta.ReceivedPackets, secondes)
                + (taux is { } x ? Docteur.T("PI.Taux", x) : "")
                + Docteur.T("PI.Carte"));
    }

    /// <summary>Des trames ignorées comptent à partir de ce nombre, et de cette part du trafic dans ce sens : un choix de DrDXT.</summary>
    public const int IgnoresMinimum = 5;
    public const double IgnoresMinimumPourcent = 0.1;

    private static bool IgnoresSignificatifs(long ignores, long paquets)
        => ignores >= IgnoresMinimum && (paquets <= 0 || 100.0 * ignores / paquets >= IgnoresMinimumPourcent);

    private static RepairFinding Inconnu(string pourquoi)
        => new(Domaine, Nom, null, null, RepairStatus.Unknown, pourquoi);
}
