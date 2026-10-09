using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Le plan d'alimentation actif et, depuis le 06/10/2026 (chantier B4), le mode d'alimentation de Windows 11.
///
/// Source : Microsoft Learn, « Customize the Windows performance power slider » (mise à jour du 25/02/2025) : « The slider
/// will appear on a device only when the Balanced power plan, or any plan that is derived from Balanced, is selected. » Le
/// mode se juge donc par la personnalité du plan (revue F-12), pas par son GUID ; « Meilleure autonomie » = l'overlay
/// 961cc777-2547-4f9d-8174-7d86181b8a7a ; sur un PC sans batterie, aucun réglage n'y est attaché par défaut. Aucun gain en
/// images n'est publié pour l'un ou l'autre : rien n'est chiffré.
/// </summary>
public static class ModeAlimentation
{
    public const string PlanEquilibre = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PlanEconomie = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string ModeMeilleureAutonomie = "961cc777-2547-4f9d-8174-7d86181b8a7a";

    public static string? NomDuPlan(string g) => g switch
    {
        PlanEquilibre => "Equilibre",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Hautes performances",
        PlanEconomie => "Économie d'énergie",
        "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Performances ultimes",
        _ => null,
    };

    public static RepairFinding JugerPlan(string? planGuid) => planGuid switch
    {
        null => new RepairFinding("POWER", "plan d'alimentation actif", null, null, RepairStatus.Unknown,
            "le plan actif n'a pas pu être lu"),
        PlanEquilibre => new RepairFinding("POWER", "plan d'alimentation actif", "Equilibre", "Equilibre",
            RepairStatus.ExpectedDefault, "c'est le plan que Windows installe par défaut : rien à réparer ici"),
        // Un plan choisi exprès pour économiser : pour jouer, il règle le processeur dans le mauvais sens.
        PlanEconomie => new RepairFinding("POWER", "plan d'alimentation actif", "Économie d'énergie", "Equilibre",
            RepairStatus.NonDefault,
            "ce plan règle le processeur pour économiser plutôt que pour répondre vite (ses réglages sont lus plus bas). "
            + "Pour jouer, repasse en Équilibré : Paramètres > Système > Alimentation, ou Panneau de configuration > "
            + "Options d'alimentation. Le gain en images n'est pas mesuré.",
            RepairAction.Recommandation),
        _ => new RepairFinding("POWER", "plan d'alimentation actif", NomDuPlan(planGuid) ?? planGuid, "Equilibre",
            RepairStatus.UserChoiceProbable, "un plan différent du défaut se change à la main : c'est probablement voulu"),
    };

    /// <param name="personnalite">La personnalité du plan actif (Équilibré pour tout plan qui en dérive), sans accolades.</param>
    /// <param name="overlayAc">ActiveOverlayAcPowerScheme, sans accolades ; null = non lu ou absent.</param>
    /// <param name="batterie">null = non lu : rien n'est dit.</param>
    public static IEnumerable<RepairFinding> JugerMode(string? personnalite, string? overlayAc, bool? batterie)
    {
        if (personnalite != PlanEquilibre || !string.Equals(overlayAc, ModeMeilleureAutonomie, StringComparison.OrdinalIgnoreCase)
            || batterie is null)
            yield break;
        yield return batterie.Value
            ? new RepairFinding("POWER", "mode d'alimentation (secteur)", "Meilleure autonomie", "Performances équilibrées",
                RepairStatus.NonDefault,
                "branché sur secteur, Windows reste réglé pour économiser la batterie : il fait pencher le processeur vers "
                + "l'économie. Pour jouer branché, choisis « Performances équilibrées » ou « Meilleures performances » dans "
                + "Paramètres > Système > Alimentation et batterie. Le gain en images n'est pas mesuré.",
                RepairAction.Recommandation)
            : new RepairFinding("POWER", "mode d'alimentation (secteur)", "Meilleure autonomie", null, RepairStatus.Observed,
                "sur un PC sans batterie, Microsoft n'attache aucun réglage par défaut à ce mode : seul le constructeur peut "
                + "lui en donner un. Son effet ici n'est pas connu.");
    }
}
