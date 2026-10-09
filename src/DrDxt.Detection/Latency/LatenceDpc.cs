using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Detection.Latency;

/// <summary>
/// La latence DPC/ISR, pendant la partie — le constat « micro-saccades » que le scan ne faisait pas.
///
/// Decision du 09/09 (plan interne, non publie) : le traceur restait un outil, a rouvrir « le jour ou une surface a
/// la demande existe ». Elle existe : le scan est lance par l'utilisateur, en jeu, et les angles morts
/// s'affichent (§3bis). La fenetre est celle des 20 s de mesure EN JEU : les pilotes qui parlent sous
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
            return Inconnu("une session noyau exige l'administrateur, et DrDXT n'en a pas les droits");
        if (trace.Issue != DpcOutcome.Ok)
            return Inconnu(trace.Issue == DpcOutcome.AucunEvenement
                ? "session ouverte mais muette : une autre trace noyau (xperf, WPR) tient peut-être la place"
                : $"trace indisponible (code Win32 {trace.CodeWin32})");

        // Des evenements recus mais aucune duree decodee ressemble trait pour trait a « aucun DPC
        // couteux » : c'est un trou de lecture, pas une machine saine.
        var pire = trace.Couts.FirstOrDefault();
        if (pire is null)
            return Inconnu($"{trace.EvenementsTotal} événements reçus, aucune durée décodée");

        var secondes = Math.Round(trace.DureeObserveeSecondes);
        var releve = F($"pire pic {pire.MaxMs:0.000} ms ({pire.Module})");
        return pire.MaxMs < SeuilMs
            ? new RepairFinding(Domaine, Nom, releve, $"< {SeuilMs:0} ms", RepairStatus.ExpectedDefault,
                // 25/09/2026 : « en jeu » retiré. Le scan peut tourner au bureau (l'écran propose « je ne
                // suis pas en jeu ») et ce classement ne sait pas si un jeu tournait.
                $"fenêtre de {secondes} s pendant la mesure : aucun DPC ni ISR au-delà d'1 ms "
                + F($"({trace.PartMachinePercent:0.00} % de la machine)"))
            : new RepairFinding(Domaine, Nom, releve, $"< {SeuilMs:0} ms", RepairStatus.Suspicious,
                $"{pire.AuDelaDeUneMs} occurrence(s) au-delà d'1 ms en {secondes} s. À 144 Hz une image dure "
                + "6,9 ms : un pic de cette taille la met en danger. " + Remede(pire.Module)
                + ". DrDXT ne touche pas aux pilotes.");
    }

    /// <summary>`Wdf01000.sys` est le CADRE KMDF de Microsoft : les DPC des pilotes ecrits dessus (USB,
    /// HID, audio, reseau...) passent par ses routines. Le nommer comme fautif enverrait mettre a jour le
    /// mauvais fichier — vu le 11/09/2026 : 5,8 ms attribues a Wdf01000.sys sur la machine de reference.</summary>
    public static string Remede(string module) =>
        string.Equals(module, "Wdf01000.sys", StringComparison.OrdinalIgnoreCase)
            ? "Wdf01000.sys est le cadre KMDF de Windows, pas le fautif : le vrai pilote (USB, HID, audio, "
              + "réseau...) est derrière lui et cette mesure ne sait pas le nommer"
            : $"Le remède est le pilote de {module} (mise à jour ou version antérieure)";

    private static RepairFinding Inconnu(string pourquoi)
        => new(Domaine, Nom, null, null, RepairStatus.Unknown, pourquoi);
}
