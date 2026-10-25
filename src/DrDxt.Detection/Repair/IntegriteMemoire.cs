using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// L'intégrité de la mémoire (HVCI), dite dans les deux sens, sans rien écrire.
///
/// Lue par la classe WMI de Microsoft <c>Win32_DeviceGuard</c> (root\Microsoft\Windows\DeviceGuard) :
/// <c>VirtualizationBasedSecurityStatus</c> 2 = la sécurité basée sur la virtualisation tourne ;
/// <c>SecurityServicesRunning</c> contient 2 quand l'intégrité de la mémoire tourne (0 = aucun service).
/// Machine de référence, 04/10 : VBS 2, services {0}, donc HVCI coupée.
///
/// Coût : une seule mesure de média, environ 8 % d'images (ComputerBase, Ryzen 5800X3D, reprise par Neowin) ; le reste
/// des chiffres qui circulent vient de blogs de réglages. Depuis Windows 11 22H2, la liste de
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
        // Non lu se dit, une famille muette ressemble à une famille saine.
        if (vbs is null || services is null)
        {
            yield return new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", null, null, RepairStatus.Unknown,
                Docteur.T("IH.NonLu"));
            yield break;
        }
        var hvci = services.Contains(ServiceHvci);
        yield return hvci
            // Active d'office sur un Windows 11 installé neuf, éteinte par défaut sur Windows 10 ou une mise à niveau : ni
            // choix ni défaut ne se dit à coup sûr, l'état est dit tel quel.
            ? new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", Docteur.T("IH.Active"), null, RepairStatus.Observed,
                Docteur.T("IH.ActiveExplication"))
            : new RepairFinding("SECURITE", "intégrité de la mémoire (HVCI)", Docteur.T("IH.Coupee"), null, RepairStatus.Observed,
                Docteur.T("IH.CoupeeExplication"));
    }
}
