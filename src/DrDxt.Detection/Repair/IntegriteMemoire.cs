using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 04/10/2026 (plan 2.5) : l'intégrité de la mémoire (HVCI), dite dans les deux sens, sans rien écrire.
///
/// Lue par la classe WMI de Microsoft <c>Win32_DeviceGuard</c> (root\Microsoft\Windows\DeviceGuard) :
/// <c>VirtualizationBasedSecurityStatus</c> 2 = la sécurité basée sur la virtualisation tourne ;
/// <c>SecurityServicesRunning</c> contient 2 quand l'intégrité de la mémoire tourne (0 = aucun service).
/// Machine de référence, 04/10 : VBS 2, services {0}, donc HVCI coupée.
///
/// Coût : une seule mesure de média, environ 8 % d'images (ComputerBase, Ryzen 5800X3D, reprise par Neowin) ; le reste
/// des chiffres qui circulent vient de blogs de réglages. Revue du 04/10 (P1) : depuis Windows 11 22H2, la liste de
/// blocage des pilotes vulnérables a son propre interrupteur, active par défaut (Microsoft Learn) ; elle ne dépend plus
/// de l'intégrité de la mémoire, la phrase ne le dit donc plus. Ce que l'intégrité de la mémoire ajoute : empêcher un
/// code noyau non signé ou modifié de s'exécuter. Certains anti-triches l'exigent. DrDXT ne la coupe ni ne l'active.
/// </summary>
public static class IntegriteMemoire
{
    private const int ServiceHvci = 2;

    /// <summary>Pur. <c>vbs</c> et <c>services</c> tels que WMI les rend ; non lus : rien.</summary>
    public static IEnumerable<RepairFinding> Juger(int? vbs, IReadOnlyList<int>? services)
    {
        // Revue adverse du 09/10/2026 (R7) : non lu se dit, une famille muette ressemble à une famille saine.
        if (vbs is null || services is null)
        {
            yield return new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", null, null, RepairStatus.Unknown,
                "l'état de l'intégrité de la mémoire n'a pas pu être lu.");
            yield break;
        }
        var hvci = services.Contains(ServiceHvci);
        yield return hvci
            ? new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", "active", null, RepairStatus.UserChoiceProbable,
                "l'intégrité de la mémoire est active : elle empêche du code noyau non signé ou modifié de s'exécuter. Son coût en "
                + "jeu n'a qu'une mesure publiée par un média, environ 8 % d'images sur un Ryzen 5800X3D ; le reste des chiffres vient "
                + "de blogs. DrDXT ne la coupe pas : c'est une protection, et certains anti-triches l'exigent. Si tu veux juger ce "
                + "qu'elle coûte chez toi, relève tes parties avant et après.")
            : new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", "coupée", null, RepairStatus.Observed,
                "l'intégrité de la mémoire est coupée : elle ne coûte rien en jeu. La contrepartie, à connaître : Windows ne vérifie "
                + "plus que le code qui tourne dans le noyau est signé et intact, et certains anti-triches peuvent la demander.");
    }
}
