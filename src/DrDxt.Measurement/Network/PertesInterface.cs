using static DrDxt.Core.TexteFrancais;
using System.Net.NetworkInformation;
using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Les pertes a l'interface, pendant une fenetre — le constat reseau que le scan de l'App ne faisait pas.
///
/// Remonte de `tools/repairaudit` (branchement du 10/09/2026, revue : « le reseau = 0 bras ») : l'outil
/// et l'App classent la meme fenetre de la meme facon. Dans l'App, la fenetre est celle de la mesure EN
/// JEU du scan : c'est la carte pendant une partie, pas une carte au repos.
///
/// Ceci mesure la CARTE — cable, port, pilote qui jette des trames — et non la route : un lien peut
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
        if (!avant.IsOk) return Inconnu(avant.Error?.Message ?? "compteurs illisibles");

        if (attendre is null) Thread.Sleep(fenetre); else attendre(fenetre);

        var apres = source.Read(interfaceId);
        if (!apres.IsOk) return Inconnu(apres.Error?.Message ?? "compteurs illisibles");

        return Classer(NicErrorCounters.Diff(avant.Value!, apres.Value!), fenetre);
    }

    /// <summary>Une difference de compteurs, un constat. Un compteur qui a recule n'est jamais « rien a
    /// signaler » : la carte a ete reinitialisee et l'information est perdue.</summary>
    public static RepairFinding Classer(NicErrorDelta delta, TimeSpan fenetre)
    {
        var secondes = Math.Round(fenetre.TotalSeconds);

        if (delta.CounterReset)
            return Inconnu("un compteur a reculé pendant la fenêtre : la carte a été réinitialisée et "
                           + "l'information est perdue. Répondre « rien à signaler » ici serait une bonne "
                           + "nouvelle fabriquee");

        var taux = delta.ReceivedDiscardPercent;
        return delta.IsClean
            ? new RepairFinding(Domaine, Nom, "aucune", "aucune", RepairStatus.ExpectedDefault,
                $"fenêtre de {secondes} s : {delta.ReceivedPackets} paquets reçus, {delta.SentPackets} émis, "
                + "zéro ignoré et zéro erreur")
            : new RepairFinding(Domaine, Nom,
                $"{delta.ReceivedDiscarded} ignorés / {delta.ReceivedErrors} erreurs en réception",
                "aucune", RepairStatus.Suspicious,
                $"sur {delta.ReceivedPackets} paquets reçus en {secondes} s"
                + (taux is { } x ? F($" ({x:0.000} %)") : "")
                + ". Ceci mesure la carte (câble, port, pilote) et non la route : un lien peut "
                + "pinguer parfaitement en jetant des trames à l'interface");
    }

    private static RepairFinding Inconnu(string pourquoi)
        => new(Domaine, Nom, null, null, RepairStatus.Unknown, pourquoi);
}
