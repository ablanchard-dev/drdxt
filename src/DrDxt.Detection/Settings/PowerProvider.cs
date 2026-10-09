using DrDxt.Core;

namespace DrDxt.Detection.Settings;

public interface IPowerProvider
{
    Result<string> GetActiveSchemeGuid();

    /// <summary>07/10/2026 (L-CPU) : le mode de boost secteur de ce plan et le défaut de sa personnalité ; null = non lu.</summary>
    ModeDeBoostLu? LireModeDeBoost(Guid plan) => null;
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
}
