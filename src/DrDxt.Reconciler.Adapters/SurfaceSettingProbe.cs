using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// SurfaceSettingProbe — ISettingProbe over the descriptor catalog.
//
// CanProbe(tweakId) = the catalog has a reconcilable descriptor for the id AND its
// surface is one this probe can actually read (Registry with a concrete leaf,
// PowerPlan, or Service). ScheduledTask / Firmware / unmapped → false.
//
// ReadCurrentValueAsync dispatches by surface and returns the value already
// CANONICALIZED by the raw reader (same JSON shape the manifest stores as
// DesiredValueJson), so the reconciler's ordinal desired-vs-actual comparison holds:
//   * Registry      → read the leaf, serialize per its kind,
//   * PowerPlan      → the active scheme GUID,
//   * Service        → the run-state token,
//   * ScheduledTask  → Fail "not supported in V1" (no scheduled-task op leaf yet),
//   * unknown / Registry-without-leaf → Fail(ProbeFailed).
//
// The three raw OS reads sit behind internal seams (IRawRegistryValueReader /
// IRawPowerPlanReader / IRawServiceStateReader) so this dispatch + the descriptor
// resolution are fully unit-tested with fakes; the real readers are the untested edge.
// -----------------------------------------------------------------------
public sealed class SurfaceSettingProbe : ISettingProbe
{
    private readonly IReconcilableTweakCatalog _catalog;
    private readonly IRawRegistryValueReader _registry;
    private readonly IRawPowerPlanReader _power;
    private readonly IRawServiceStateReader _service;

    /// <summary>Production constructor — the real OS readers behind each surface.</summary>
    public SurfaceSettingProbe(IReconcilableTweakCatalog catalog)
        : this(catalog,
               new Win32RegistryValueReader(),
               new PInvokePowerPlanReader(),
               new ServiceControllerStateReader())
    {
    }

    internal SurfaceSettingProbe(
        IReconcilableTweakCatalog catalog,
        IRawRegistryValueReader registry,
        IRawPowerPlanReader power,
        IRawServiceStateReader service)
    {
        _catalog = catalog;
        _registry = registry;
        _power = power;
        _service = service;
    }

    public bool CanProbe(string tweakId)
    {
        var d = _catalog.DescriptorFor(tweakId);
        if (d is null)
            return false;

        return d.Surface switch
        {
            ReconcileSurface.Registry => d.Registry is not null,
            ReconcileSurface.PowerPlan => true,
            ReconcileSurface.Service => !string.IsNullOrEmpty(d.ServiceName),
            _ => false, // ScheduledTask / Firmware — not readable in V1.
        };
    }

    public Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct)
    {
        var d = _catalog.DescriptorFor(tweakId);
        if (d is null)
            return Fail($"No reconcilable descriptor for tweak '{tweakId}'.");

        switch (d.Surface)
        {
            case ReconcileSurface.Registry:
                if (d.Registry is null)
                    return Fail($"Registry descriptor for '{tweakId}' has no read location.");
                return Task.FromResult(_registry.ReadCanonical(d.Registry));

            case ReconcileSurface.PowerPlan:
                return Task.FromResult(_power.ReadActiveSchemeCanonical());

            case ReconcileSurface.Service:
                if (string.IsNullOrEmpty(d.ServiceName))
                    return Fail($"Service descriptor for '{tweakId}' has no service name.");
                return Task.FromResult(_service.ReadStateCanonical(d.ServiceName));

            case ReconcileSurface.ScheduledTask:
                return Task.FromResult(Result<string>.Fail(
                    ReconcilerErrorCodes.ProbeFailed,
                    $"Scheduled-task surface for '{tweakId}' is not supported in V1 (no op leaf)."));

            default:
                return Fail($"Surface '{d.Surface}' for '{tweakId}' is not readable.");
        }
    }

    private static Task<Result<string>> Fail(string message)
        => Task.FromResult(Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, message));
}
