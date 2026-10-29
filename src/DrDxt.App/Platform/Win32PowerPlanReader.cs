using System.Runtime.Versioning;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Win32PowerPlanReader : le vrai IPowerPlanReader, lecture seule. Le GUID du plan actif par PlanActif (Core), seul appel
// de PowerGetActiveScheme ; échec → Fail(TWEAK.SDK_UNAVAILABLE). Changer de plan passe par le helper
// (SetActivePowerPlanOp).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32PowerPlanReader : IPowerPlanReader
{
    public Result<Guid> GetActivePowerPlan()
    {
        var (plan, rc, exception) = PlanActif.Lire();
        if (plan is { } guid) return Result<Guid>.Ok(guid);
        return Result<Guid>.Fail(ErrorCodes.TweakSdkUnavailable, exception is null
            ? $"PowerGetActiveScheme failed (Win32 {rc})."
            : "Active power-plan read failed.");
    }

    public Result<ModeDeBoostLu> LireModeDeBoost(Guid plan) =>
        DrDxt.Detection.Settings.PowerSettingsReader.LireModeDeBoost(plan) is { } lu
            ? Result<ModeDeBoostLu>.Ok(lu)
            : Result<ModeDeBoostLu>.Fail(ErrorCodes.TweakSdkUnavailable, "Boost mode (PERFBOOSTMODE) unread.");

    public Result<EconomieSansFilLue> LireEconomieSansFil(Guid plan) =>
        DrDxt.Detection.Settings.PowerSettingsReader.LireEconomieSansFil(plan) is { } lu
            ? Result<EconomieSansFilLue>.Ok(lu)
            : Result<EconomieSansFilLue>.Fail(ErrorCodes.TweakSdkUnavailable, "Wireless power saving mode unread.");
}
