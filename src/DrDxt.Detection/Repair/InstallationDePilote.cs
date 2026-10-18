using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Une installation de pilote graphique qui échoue sans raison visible : une stratégie de restriction d'installation de
/// périphériques peut l'interdire. DrDXT lit ces règles et les applique à l'identifiant d'instance, aux identifiants matériels
/// et compatibles et à la classe de CHAQUE carte, pour nommer la bonne (une règle qui vise la puce intégrée ne concerne pas la
/// carte dédiée). Il ne les écrit ni ne les retire jamais : elles appartiennent à l'administrateur de la machine ou à l'outil
/// qui les a posées.
/// Sources : Microsoft Learn, « Policy CSP - DeviceInstallation » (clé Software\Policies\Microsoft\Windows\DeviceInstall\
/// Restrictions : DenyInstanceIDs, AllowInstanceIDs, DenyDeviceIDs, AllowDeviceIDs, DenyDeviceClasses, AllowDeviceClasses,
/// chacune avec sa liste dans la sous-clé du même nom ; DenyDeviceIDsRetroactive ; DenyUnspecified ; AllowDenyLayered) et
/// « Manage Device Installation with Group Policy » (par défaut, toute règle « Empêcher » a priorité sur celles qui
/// autorisent ; en couches, instance > identifiant > classe, le refus avant l'autorisation dans chaque couche, et « non
/// décrits » est ignoré) ; « Windows Update policy » (ExcludeWUDriversInQualityUpdate).
/// </summary>
public static class InstallationDePilote
{
    /// <summary>Les règles lues ; une liste dont la règle n'est pas activée est vide.</summary>
    public sealed record Regles(bool EnCouches, IReadOnlyList<string> InstancesInterdites, IReadOnlyList<string> InstancesAutorisees,
        IReadOnlyList<string> IdsInterdits, IReadOnlyList<string> IdsAutorises, IReadOnlyList<string> ClassesInterdites,
        IReadOnlyList<string> ClassesAutorisees, bool NonDecritsInterdits, bool Retroactif, bool PilotesHorsWindowsUpdate);

    public sealed record Carte(string Nom, IReadOnlyList<string> Identifiants, string? Instance = null);

    // La classe Affichage (Display) de Windows.
    internal const string ClasseAffichage = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Le vrai, en lecture seule : la clé de stratégie (lisible sans droits) ; <c>null</c> = registre refusé.</summary>
    public static Regles? Lire()
    {
        try
        {
            using var r = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions");
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            static bool Actif(RegistryKey? k, string nom) => k?.GetValue(nom) is int v && v != 0;
            static IReadOnlyList<string> Liste(RegistryKey? k, string regle)
            {
                if (!Actif(k, regle)) return [];
                using var s = k!.OpenSubKey(regle);
                return s?.GetValueNames().Select(n => s.GetValue(n) as string).OfType<string>().ToList() ?? [];
            }
            return new Regles(Actif(r, "AllowDenyLayered"), Liste(r, "DenyInstanceIDs"), Liste(r, "AllowInstanceIDs"),
                Liste(r, "DenyDeviceIDs"), Liste(r, "AllowDeviceIDs"), Liste(r, "DenyDeviceClasses"), Liste(r, "AllowDeviceClasses"),
                Actif(r, "DenyUnspecified"), Actif(r, "DenyDeviceIDsRetroactive"), Actif(wu, "ExcludeWUDriversInQualityUpdate"));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : ce qui interdit le pilote de cette carte (l'identifiant visé, ou la règle) ; <c>null</c> si rien.</summary>
    public static string? Bloque(Regles r, Carte c)
    {
        string? Instance(IReadOnlyList<string> l) => c.Instance is { } i && l.Contains(i, StringComparer.OrdinalIgnoreCase) ? i : null;
        string? Id(IReadOnlyList<string> l) => l.FirstOrDefault(x => c.Identifiants.Contains(x, StringComparer.OrdinalIgnoreCase));
        static bool Classe(IReadOnlyList<string> l) => l.Contains(ClasseAffichage, StringComparer.OrdinalIgnoreCase);
        var parClasse = Docteur.T("IP.ParClasse");

        if (r.EnCouches)
        {
            if (Instance(r.InstancesInterdites) is { } a) return a;
            if (Instance(r.InstancesAutorisees) is not null) return null;
            if (Id(r.IdsInterdits) is { } b) return b;
            if (Id(r.IdsAutorises) is not null) return null;
            return Classe(r.ClassesInterdites) ? parClasse : null;
        }
        if ((Instance(r.InstancesInterdites) ?? Id(r.IdsInterdits) ?? (Classe(r.ClassesInterdites) ? parClasse : null)) is { } refus)
            return refus;
        return r.NonDecritsInterdits && Instance(r.InstancesAutorisees) is null && Id(r.IdsAutorises) is null && !Classe(r.ClassesAutorisees)
            ? Docteur.T("IP.NonDecrit") : null;
    }

    /// <summary>Pur. Les règles appliquées à chaque carte ; une règle qui ne vise aucune carte est dite, sans alarme.</summary>
    public static IEnumerable<RepairFinding> Juger(Regles? regles, IReadOnlyList<Carte>? cartes)
    {
        const string Nom = "installation de pilote bloquee";
        if (regles is null || cartes is null)
        {
            yield return new RepairFinding("GPU DRIVER", Nom, null, null, RepairStatus.Unknown, Docteur.T("IP.NonLu"));
            yield break;
        }
        var visees = cartes.Select(c => Bloque(regles, c) is { } raison ? Docteur.T("IP.Carte", c.Nom, raison) : null).OfType<string>().ToList();
        var interdits = regles.IdsInterdits.Concat(regles.InstancesInterdites).ToList();
        if (visees.Count > 0)
            yield return new RepairFinding("GPU DRIVER", Nom, string.Join(", ", visees), Docteur.T("IP.Aucune"), RepairStatus.Suspicious,
                Docteur.T("IP.Bloquee", string.Join(", ", visees)) + (regles.Retroactif ? Docteur.T("IP.Retroactif") : ""),
                RepairAction.Recommandation, Remede: Docteur.T("IP.Remede"));
        else if (interdits.Count > 0)
            yield return new RepairFinding("GPU DRIVER", Nom, Docteur.T("IP.AutreMateriel", interdits.Count), Docteur.T("IP.Aucune"),
                RepairStatus.Observed, Docteur.T("IP.AutreMaterielExplication", string.Join(", ", interdits)));
        else
            yield return new RepairFinding("GPU DRIVER", Nom, Docteur.T("IP.Aucune"), Docteur.T("IP.Aucune"), RepairStatus.ExpectedDefault,
                Docteur.T("IP.AucuneExplication"));
        if (regles.PilotesHorsWindowsUpdate)
            yield return new RepairFinding("GPU DRIVER", "pilotes exclus de Windows Update", Docteur.T("IP.Exclus"), null, RepairStatus.Observed,
                Docteur.T("IP.ExclusExplication"));
    }
}
