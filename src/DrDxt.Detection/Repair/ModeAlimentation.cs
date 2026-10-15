using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Le plan d'alimentation actif et le mode d'alimentation de Windows 11.
///
/// Source : Microsoft Learn, « Customize the Windows performance power slider » (mise à jour du 25/02/2025) : « The slider
/// will appear on a device only when the Balanced power plan, or any plan that is derived from Balanced, is selected. » Le
/// mode se juge donc par la personnalité du plan, pas par son GUID ; « Meilleure autonomie » = l'overlay
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
        PlanEquilibre => Docteur.T("MA.Equilibre"),
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => Docteur.T("MA.Hautes"),
        PlanEconomie => Docteur.T("MA.Economie"),
        "e9a42b02-d5df-448d-aa00-03f14749eb61" => Docteur.T("MA.Ultimes"),
        _ => null,
    };

    public static RepairFinding JugerPlan(string? planGuid) => planGuid switch
    {
        null => new RepairFinding("POWER", "plan d'alimentation actif", null, null, RepairStatus.Unknown,
            Docteur.T("MA.NonLu")),
        PlanEquilibre => new RepairFinding("POWER", "plan d'alimentation actif", Docteur.T("MA.Equilibre"), Docteur.T("MA.Equilibre"),
            RepairStatus.ExpectedDefault, Docteur.T("MA.Defaut")),
        // Un plan choisi exprès pour économiser : pour jouer, il règle le processeur dans le mauvais sens.
        PlanEconomie => new RepairFinding("POWER", "plan d'alimentation actif", Docteur.T("MA.Economie"), Docteur.T("MA.Equilibre"),
            RepairStatus.NonDefault, Docteur.T("MA.EconomieExplication"),
            RepairAction.Recommandation),
        _ => new RepairFinding("POWER", "plan d'alimentation actif", NomDuPlan(planGuid) ?? planGuid, Docteur.T("MA.Equilibre"),
            RepairStatus.UserChoiceProbable, Docteur.T("MA.Autre")),
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
            ? new RepairFinding("POWER", "mode d'alimentation (secteur)", Docteur.T("MA.MeilleureAutonomie"), Docteur.T("MA.Equilibrees"),
                RepairStatus.NonDefault, Docteur.T("MA.SecteurBatterie"),
                RepairAction.Recommandation)
            : new RepairFinding("POWER", "mode d'alimentation (secteur)", Docteur.T("MA.MeilleureAutonomie"), null, RepairStatus.Observed,
                Docteur.T("MA.SansBatterie"));
    }
}
