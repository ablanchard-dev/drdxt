using static DrDxt.Core.TexteFrancais;
using System.Net.NetworkInformation;
using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Les pertes a l'interface, pendant une fenetre : un constat reseau du scan de l'App.
///
/// Remonte de `tools/repairaudit` : l'outil
/// et l'App classent la meme fenetre de la meme facon. Dans l'App, la fenetre est celle de la mesure en
/// jeu du scan : c'est la carte pendant une partie, pas une carte au repos.
///
/// Ceci mesure la carte (cable, port, pilote qui jette des trames) et non la route : un lien peut
/// pinguer parfaitement en perdant des paquets a l'interface.
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
        return delta.IsClean
            ? new RepairFinding(Domaine, Nom, Docteur.T("PI.Aucune"), Docteur.T("PI.Aucune"), RepairStatus.ExpectedDefault,
                Docteur.T("PI.Propre", secondes, delta.ReceivedPackets, delta.SentPackets))
            : new RepairFinding(Domaine, Nom,
                Docteur.T("PI.Pertes", delta.ReceivedDiscarded, delta.ReceivedErrors),
                Docteur.T("PI.Aucune"), RepairStatus.Suspicious,
                Docteur.T("PI.Sur", delta.ReceivedPackets, secondes)
                + (taux is { } x ? Docteur.T("PI.Taux", x) : "")
                + Docteur.T("PI.Carte"));
    }

    private static RepairFinding Inconnu(string pourquoi)
        => new(Domaine, Nom, null, null, RepairStatus.Unknown, pourquoi);
}
