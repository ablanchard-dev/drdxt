using DrDxt.Core;
using DrDxt.Detection.Settings;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 07/10/2026 (L-USB) : la suspension sélective USB, en constat seul. Aucun levier, et c'est une décision sourcée.
///
/// Microsoft Learn, « USB Selective Suspend » (07/05/2025) : la suspension coupe un port seulement quand le pilote du
/// périphérique dit qu'il est inactif ; « Selective suspend is enabled by default. Microsoft strongly recommends not
/// disabling selective suspend. » Microsoft Learn, « Selective Suspend for HID Over USB Devices » (11/01/2024) : pour les
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

    private const string CeQueFaitLePlan =
        "ce réglage ne suspend un périphérique USB que lorsqu'il est inactif et que son pilote le demande ; Microsoft "
        + "recommande de ne pas le couper. Les souris, claviers et manettes en sont exclus par défaut (ligne des "
        + "périphériques d'entrée).";

    public static string Lisible(uint valeur) => valeur switch
    {
        0 => "désactivée",
        1 => "activée",
        _ => valeur.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Le réglage du plan actif sur secteur. Aucun geste : ni le couper ni le remettre n'a de gain sourcé.</summary>
    public static RepairFinding JugerPlan(PowerSettingValue lu) => lu switch
    {
        { ValeurAc: null } => new RepairFinding(Domaine, lu.Nom, null, null, RepairStatus.Unknown,
            "le réglage du plan actif n'a pas pu être lu"),
        { EcarteDuDefaut: false } => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), Lisible(lu.DefautAc!.Value),
            RepairStatus.ExpectedDefault, "c'est la valeur que Windows met dans ce plan : " + CeQueFaitLePlan),
        { EcarteDuDefaut: true } => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), Lisible(lu.DefautAc!.Value),
            RepairStatus.NonDefault, "ce plan ne porte plus la valeur que Windows y met. Aucune source ne chiffre un gain en "
            + "jeu dans un sens ou dans l'autre : DrDXT ne propose rien. " + CeQueFaitLePlan),
        _ => new RepairFinding(Domaine, lu.Nom, Lisible(lu.ValeurAc.Value), null, RepairStatus.Observed,
            "le défaut de ce plan n'a pas pu être lu. " + CeQueFaitLePlan),
    };

    /// <param name="branches">Les périphériques d'entrée USB (pilote HidUsb) branchés ; null = liste non lue.</param>
    /// <param name="suspendus">Ceux dont <c>SelectiveSuspendEnabled</c> vaut 1. Absent compte comme non activé : la
    /// section par défaut d'input.inf ne le pose pas, seule la section « HID_SelSus_Inst » d'un constructeur le pose.</param>
    public static RepairFinding JugerEntrees(int? branches, int suspendus) => branches switch
    {
        null => new RepairFinding(Domaine, NomEntrees, null, null, RepairStatus.Unknown,
            "la liste des périphériques d'entrée USB n'a pas pu être lue"),
        0 => new RepairFinding(Domaine, NomEntrees, "aucun périphérique d'entrée USB branché", null, RepairStatus.Observed,
            "aucune souris, aucun clavier, aucune manette USB n'a été vu branché : rien à dire ici"),
        _ when suspendus == 0 => new RepairFinding(Domaine, NomEntrees, $"activée sur 0 des {branches}", "activée sur aucun",
            RepairStatus.ExpectedDefault,
            "Windows laisse la suspension coupée sur les souris, claviers et manettes USB, pour éviter le délai de reprise "
            + "(Microsoft Learn) : le réglage du plan ne les met donc pas en pause."),
        _ => new RepairFinding(Domaine, NomEntrees, $"activée sur {suspendus} des {branches}", "activée sur aucun",
            RepairStatus.Observed,
            "le constructeur de ces périphériques a activé leur suspension. Microsoft exige alors qu'ils gardent la "
            + "première frappe ou le premier mouvement et reprennent sans délai perçu ; aucun délai n'est mesuré ici."),
    };

    /// <summary>La valeur de registre <c>SelectiveSuspendEnabled</c> : binaire (un octet) ou DWORD selon l'INF.</summary>
    public static bool Activee(object? valeur) => valeur switch
    {
        byte[] { Length: > 0 } octets => octets[0] == 1,
        int n => n == 1,
        _ => false,
    };
}
