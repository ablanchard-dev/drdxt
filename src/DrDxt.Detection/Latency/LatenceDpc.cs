using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Detection.Latency;

/// <summary>
/// La latence DPC/ISR, pendant la partie : le constat « micro-saccades ».
///
/// Le traceur sert ici parce qu'une surface a la demande existe : le scan est lance par
/// l'utilisateur, en jeu, et les angles morts s'affichent. La fenetre est celle des 20 s de mesure en jeu : les pilotes qui parlent sous
/// charge, pas ceux d'un bureau au repos.
///
/// Le produit n'a aucun levier sur un pilote : le constat nomme le fautif, il ne corrige rien.
/// </summary>
public static class LatenceDpc
{
    public const string Domaine = "LATENCE";
    public const string Nom = "DPC/ISR des pilotes";

    /// <summary>Au-dela d'1 ms d'un coup, une trame de jeu est en danger (seuil de `tools/dpcscan`).</summary>
    public const double SeuilMs = 1.0;

    public static RepairFinding Classer(DpcTraceResult trace)
    {
        if (trace.Issue == DpcOutcome.DroitsInsuffisants)
            return Inconnu(Docteur.T("DPC.Droits"));
        if (trace.Issue != DpcOutcome.Ok)
            return Inconnu(trace.Issue == DpcOutcome.AucunEvenement
                ? Docteur.T("DPC.Muette")
                : Docteur.T("DPC.Indisponible", trace.CodeWin32));

        // Des evenements recus mais aucune duree decodee ressemble trait pour trait a « aucun DPC
        // couteux » : c'est un trou de lecture, pas une machine saine.
        var pire = trace.Couts.FirstOrDefault();
        if (pire is null)
            return Inconnu(Docteur.T("DPC.SansDuree", trace.EvenementsTotal));

        var secondes = Math.Round(trace.DureeObserveeSecondes);
        var releve = Docteur.T("DPC.Pire", pire.MaxMs, pire.Module);
        return pire.MaxMs < SeuilMs
            ? new RepairFinding(Domaine, Nom, releve, $"< {SeuilMs:0} ms", RepairStatus.ExpectedDefault,
                // « en jeu » retiré. Le scan peut tourner au bureau (l'écran propose « je ne
                // suis pas en jeu ») et ce classement ne sait pas si un jeu tournait.
                Docteur.T("DPC.Propre", secondes, trace.PartMachinePercent))
            : new RepairFinding(Domaine, Nom, releve, $"< {SeuilMs:0} ms", RepairStatus.Suspicious,
                Docteur.T("DPC.Pics", pire.AuDelaDeUneMs, secondes) + Remede(pire.Module)
                + Docteur.T("DPC.NeTouchePas"));
    }

    /// <summary>`Wdf01000.sys` est le cadre KMDF de Microsoft : les DPC des pilotes ecrits dessus (USB,
    /// HID, audio, reseau...) passent par ses routines. Le nommer comme fautif enverrait mettre a jour le
    /// mauvais fichier (mesure le 11/09/2026 sur la machine de reference : 5,8 ms attribues a Wdf01000.sys).</summary>
    public static string Remede(string module) =>
        string.Equals(module, "Wdf01000.sys", StringComparison.OrdinalIgnoreCase)
            ? Docteur.T("DPC.Wdf")
            : Docteur.T("DPC.Remede", module);

    private static RepairFinding Inconnu(string pourquoi)
        => new(Domaine, Nom, null, null, RepairStatus.Unknown, pourquoi);
}
