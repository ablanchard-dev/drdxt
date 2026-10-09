using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// ElevatedReapplyClient (plan §9 / T32) — the bridge from "a reconcilable tweakId
// drifted, restore it to desired" to the existing typed privileged op pipeline.
//
// SECURITY (I3, the WinRing0 lesson): re-apply never introduces a new privileged
// primitive. It resolves the tweakId+desired value to one of the existing sealed
// PrivilegedOp leaves (SetRegistryValueOp / SetActivePowerPlanOp /
// RunServiceControlOp / ApplyNvApiDrsSettingOp / …) via the injected
// IReapplyOpMapper, then dispatches it through the same IElevatedClient.ExecuteAsync
// — inheriting its full signature → caller-authz → server-side allowlist path. A
// tweakId the mapper does not know fails before any IPC (no op is ever sent).
//
// The op MAPPINGS themselves (which tweakId builds which leaf with which bounded
// value) mirror the Application-layer tweaks; wiring the production mapper to the
// real catalog is the NEXT increment. Here the seam + dispatch + the fail-before-IPC
// guarantee are built and tested.
// -----------------------------------------------------------------------

/// <summary>Resolves a reconcilable tweakId + its desired serialized value to the
/// Existing typed PrivilegedOp leaf that re-asserts it. An unknown tweakId, or a
/// desired value that does not parse into the bounded op fields, → Fail (so the
/// reapply client refuses before any IPC). Never returns a free/generic op.</summary>
public interface IReapplyOpMapper
{
    Result<PrivilegedOp> Map(string tweakId, string desiredValueJson);
}

public sealed class ElevatedReapplyClient : IElevatedReapplyClient
{
    private readonly IReapplyOpMapper _mapper;
    private readonly IElevatedClient _elevated;

    public ElevatedReapplyClient(IReapplyOpMapper mapper, IElevatedClient elevated)
    {
        _mapper = mapper;
        _elevated = elevated;
    }

    public async Task<Result> ReapplyAsync(string tweakId, string desiredValueJson, CancellationToken ct)
    {
        // (1) Resolve to an existing typed op. Unknown / unbindable → fail before IPC.
        var op = _mapper.Map(tweakId, desiredValueJson);
        if (!op.IsOk)
            return Result.Fail(
                op.Error!.Value.Code,
                $"Cannot re-apply '{tweakId}': {op.Error.Value.Message}");

        // (2) Dispatch through the same elevated pipeline (no new privileged primitive).
        return await _elevated.ExecuteAsync(op.Value!, ct);
    }
}
