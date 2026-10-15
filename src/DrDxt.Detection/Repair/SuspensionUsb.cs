using DrDxt.Core;
using DrDxt.Detection.Settings;

namespace DrDxt.Detection.Repair;

/// <summary>
/// La suspension sélective USB, en constat seul. Aucun levier, et c'est une décision sourcée.
///
/// Microsoft Learn, « USB Selective Suspend » : la suspension coupe un port seulement quand le pilote du
/// périphérique dit qu'il est inactif ; « Selective suspend is enabled by default. Microsoft strongly recommends not
/// disabling selective suspend. » Microsoft Learn, « Selective Suspend for HID Over USB Devices » : pour les
/// souris, claviers et manettes USB, « USB selective suspend is disabled by Windows in order to provide a consistent user
/// experience and to avoid resume latency » ; seul le constructeur l'active (descripteur Microsoft OS ou INF qui pose
/// <c>SelectiveSuspendEnabled</c> sous <c>Device Parameters</c>), et le périphérique doit alors garder la première frappe ou
/// le premier mouvement et reprendre « without any perceived delay ». La seule page Microsoft qui fait couper le réglage du
/// plan vise les salles Teams Rooms, pour des périphériques qui ne répondent plus au sortir de la veille du système : pas
/// la latence en jeu. Aucune source ne chiffre un gain en images ou en latence : rien n'est proposé, rien n'est chiffré.
/// </summary>
public static class SuspensionUsb
{
    /// <summary>GUID_USB_SUBGROUP et USB selective suspend setting (powercfg /q : 0 = désactivée, 1 = activée).</summary>
    public static readonly Guid SousGroupe = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid Reglage = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    public const string Domaine = "USB";
    public const string NomEntrees = "suspension sélective des périphériques d'entrée USB";

    private static string CeQueFaitLePlan => Docteur.T("SU.CeQueFait");

    public static string Lisible(uint valeur) => valeur switch
    {
        0 => Docteur.T("SU.Desactivee"),
        1 => Docteur.T("SU.Activee"),
        _ => valeur.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Le réglage du plan actif sur secteur. Aucun geste : ni le couper ni le remettre n'a de gain sourcé.</summary>
    public static RepairFinding JugerPlan(PowerSettingValue lu) => lu switch
    {
        { ValeurAc: null } => new RepairFinding(Domaine, lu.Nom, null, null, RepairStatus.Unknown,
            Docteur.T("SU.PlanNonLu")),
        { EcarteDuDefaut: false } => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), Lisible(lu.DefautAc!.Value),
            RepairStatus.ExpectedDefault, Docteur.T("SU.Defaut", CeQueFaitLePlan)),
        { EcarteDuDefaut: true } => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), Lisible(lu.DefautAc!.Value),
            RepairStatus.NonDefault, Docteur.T("SU.Ecart", CeQueFaitLePlan)),
        _ => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), null, RepairStatus.Observed,
            Docteur.T("SU.DefautNonLu", CeQueFaitLePlan)),
    };

    /// <param name="branches">Les périphériques d'entrée USB (pilote HidUsb) branchés ; null = liste non lue.</param>
    /// <param name="suspendus">Ceux dont <c>SelectiveSuspendEnabled</c> vaut 1. Absent compte comme non activé : la
    /// section par défaut d'input.inf ne le pose pas, seule la section « HID_SelSus_Inst » d'un constructeur le pose.</param>
    public static RepairFinding JugerEntrees(int? branches, int suspendus) => branches switch
    {
        null => new RepairFinding(Domaine, NomEntrees, null, null, RepairStatus.Unknown,
            Docteur.T("SU.EntreesNonLues")),
        0 => new RepairFinding(Domaine, NomEntrees, Docteur.T("SU.AucunBranche"), null, RepairStatus.Observed,
            Docteur.T("SU.AucunBrancheExplication")),
        _ when suspendus == 0 => new RepairFinding(Domaine, NomEntrees, Docteur.T("SU.ActiveeSur", 0, branches), Docteur.T("SU.ActiveeSurAucun"),
            RepairStatus.ExpectedDefault, Docteur.T("SU.WindowsCoupe")),
        _ => new RepairFinding(Domaine, NomEntrees, Docteur.T("SU.ActiveeSur", suspendus, branches), Docteur.T("SU.ActiveeSurAucun"),
            RepairStatus.Observed, Docteur.T("SU.Constructeur")),
    };

    /// <summary>La valeur de registre <c>SelectiveSuspendEnabled</c> : binaire (un octet) ou DWORD selon l'INF.</summary>
    public static bool Activee(object? valeur) => valeur switch
    {
        byte[] { Length: > 0 } octets => octets[0] == 1,
        int n => n == 1,
        _ => false,
    };
}
