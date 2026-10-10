using DrDxt.Core;

namespace DrDxt.Detection.Settings;

public interface IPowerProvider
{
    Result<string> GetActiveSchemeGuid();

    /// <summary>07/10/2026 (L-CPU) : le mode de boost secteur de ce plan et le défaut de sa personnalité ; null = non lu.</summary>
    ModeDeBoostLu? LireModeDeBoost(Guid plan) => null;

    /// <summary>B3 (10/10/2026) : l'économie d'énergie de la carte sans fil (secteur) de ce plan ; null = non lu.</summary>
    EconomieSansFilLue? LireEconomieSansFil(Guid plan) => null;
}

/// <summary>Le plan actif pour l'audit, en texte. L'appel natif vit dans <see cref="PlanActif"/>.</summary>
public sealed class PowerProvider : IPowerProvider
{
    public Result<string> GetActiveSchemeGuid()
    {
        var (plan, rc, exception) = PlanActif.Lire();
        return plan is { } guid
            ? Result<string>.Ok(guid.ToString())
            : Result<string>.Fail(ErrorCodes.PowerFail, exception ?? $"PowerGetActiveScheme rc={rc}");
    }

    public ModeDeBoostLu? LireModeDeBoost(Guid plan) => PowerSettingsReader.LireModeDeBoost(plan);
    public EconomieSansFilLue? LireEconomieSansFil(Guid plan) => PowerSettingsReader.LireEconomieSansFil(plan);
}
