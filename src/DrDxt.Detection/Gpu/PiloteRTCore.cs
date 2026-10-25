using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Le pilote noyau RTCore (installé par des outils de réglage de carte graphique) : des versions de ce pilote sont dans la
/// liste de blocage des pilotes vulnérables de Microsoft. Lecture seule ; DrDXT ne retire pas le pilote d'un autre logiciel.
/// </summary>
/// <remarks>
/// Microsoft, « Microsoft recommended driver block rules » (version du 08/10/2026) et son archive
/// aka.ms/VulnerableDriverBlockList, DriverPolicy_Enforced.xml version 10.0.29545.0 : vingt règles ID_DENY_RTCORE_*, cinq
/// fichiers RTCore avec quatre empreintes chacun (SHA-1, SHA-256 et leurs empreintes de page). Les règles sont par empreinte : DrDXT dit la présence et l'état du pilote, pas que cette copie précise
/// est bloquée. Le pilote se reconnaît à son nom ou à son fichier (RTCore). Relevé sur la machine de référence le 17/10/2026 :
/// aucun pilote RTCore. Rangé avec le pilote graphique : ce sont les outils de réglage de carte qui l'installent.
/// Lu dans la clé Services du registre, par l'énumération de l'inventaire (<c>InventaireMachine.ServicesDuRegistre</c>) :
/// mesuré sur ce PC le 17/10/2026, 10 à 13 ms pour 866 clés, contre 281 à 360 ms pour la requête WMI LIKE sur
/// Win32_SystemDriver qu'elle remplace. Le chargement n'est lu (pilotes en mémoire, psapi) que si une entrée RTCore existe.
/// </remarks>
public static class PiloteRTCore
{
    /// <summary>Pur : le premier service ou pilote RTCore, par son nom ou par son fichier.</summary>
    public static (string Nom, string Image)? Trouver(IEnumerable<(string Nom, string Image)> services)
        => services.Where(s => s.Nom.StartsWith("RTCore", StringComparison.OrdinalIgnoreCase)
                               || s.Image.Contains("RTCore", StringComparison.OrdinalIgnoreCase))
            .Select(s => ((string, string)?)s).FirstOrDefault();

    /// <param name="services">Les entrées de la clé Services ; null = non lue.</param>
    /// <param name="charge">Vrai si un pilote de ce nom de fichier est chargé en mémoire ; null quand le chargement n'a pas pu
    /// être lu.</param>
    public static RepairFinding Juger(IReadOnlyList<(string Nom, string Image)>? services, Func<string, bool?> charge)
    {
        const string nom = "pilote RTCore";
        if (services is null)
            return new RepairFinding("GPU DRIVER", nom, null, null, RepairStatus.Unknown, Docteur.T("RTC.NonLu"));
        if (Trouver(services) is not { } p)
            return new RepairFinding("GPU DRIVER", nom, Docteur.T("RTC.ValeurAbsent"), null, RepairStatus.ExpectedDefault, Docteur.T("RTC.Absent"));
        var fichier = Path.GetFileName(p.Image.Trim('"'));
        return new RepairFinding("GPU DRIVER", nom, p.Image, Docteur.T("RTC.ValeurAbsent"),
            RepairStatus.Suspicious, Docteur.T(charge(fichier) switch { true => "RTC.Charge", false => "RTC.Installe", null => "RTC.InstalleChargeNonLu" }), RepairAction.Recommandation);
    }

    /// <summary>Le pilote de ce nom de fichier est-il chargé en mémoire ? (EnumDeviceDrivers, psapi.) Null quand la liste n'a
    /// pas pu être lue, ou qu'aucun nom n'en sort : sans droits, Windows peut masquer les adresses du noyau, et « pas chargé »
    /// serait alors une affirmation sans lecture.</summary>
    public static bool? Charge(string fichier)
    {
        try
        {
            var bases = new IntPtr[2048];
            if (!Natif.EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out var octets) || octets == 0) return null;
            var tampon = new System.Text.StringBuilder(260);
            var nomsLus = 0;
            for (var i = 0; i < octets / IntPtr.Size && i < bases.Length; i++)
            {
                tampon.Clear();
                if (Natif.GetDeviceDriverBaseName(bases[i], tampon, tampon.Capacity) <= 0) continue;
                nomsLus++;
                if (string.Equals(tampon.ToString(), fichier, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return nomsLus > 0 ? false : null;
        }
        catch (Exception) { return null; }
    }
}
