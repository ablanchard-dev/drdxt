using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// DefaultReapplyOpMapper (plan §9 / T32) — the production IReapplyOpMapper, and the
// SECURITY BOUNDARY of the reconciler (I3, the WinRing0 lesson).
//
// It resolves a reconcilable tweakId + its desired serialized value to exactly ONE of
// the existing sealed PrivilegedOp leaves with BOUNDED fields, or it FAILS before any
// op is produced. It never constructs a free/generic privileged primitive.
//
// Single source of truth. The op's TARGET (hive/path/name/kind, serviceName, surface)
// is read entirely from the same IReconcilableTweakCatalog the read-side probe uses —
// so the read and write sides share ONE location table. The caller-supplied
// desiredValueJson influences only the bounded VALUE field, never the target: a hostile
// value cannot point an admin write at a different key. The value is also re-serialized
// through ReconcilableValueCanonicalizer so the op carries the byte-identical form the
// probe reads back when confirming.
//
// CLOSED by construction: an unknown tweakId (no descriptor), an unparseable / out-of-
// type value, or a surface with no op leaf in V1 (ScheduledTask / Firmware) → Fail, no
// op. Only the three V1-wired ids are mappable: latency.reflex.lowLatencyMode (Registry
// DWORD), fixit.sanePowerPlan (PowerPlan), fixit.x3dVCacheService (Service).
// -----------------------------------------------------------------------
public sealed class DefaultReapplyOpMapper : IReapplyOpMapper
{
    private readonly IReconcilableTweakCatalog _catalog;

    public DefaultReapplyOpMapper(IReconcilableTweakCatalog catalog)
        => _catalog = catalog;

    public Result<PrivilegedOp> Map(string tweakId, string desiredValueJson)
    {
        // (1) Resolve the descriptor first. An id not in the closed table is not
        // reconcilable → refuse before any value parsing or op construction (I3).
        var descriptor = _catalog.DescriptorFor(tweakId);
        if (descriptor is null)
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' has no reconcilable descriptor");

        // (2) Build the op from the descriptor's surface. The target never comes from
        // desiredValueJson — only the bounded value/identity field below does.
        return descriptor.Surface switch
        {
            ReconcileSurface.Registry => MapRegistry(tweakId, descriptor, desiredValueJson),
            ReconcileSurface.PowerPlan => MapPowerPlan(tweakId, desiredValueJson),
            ReconcileSurface.Service => MapService(tweakId, descriptor, desiredValueJson),

            // No typed op leaf re-asserts these in V1 (a scheduled task / firmware write
            // is out of the reconciler's closed set). Refuse rather than improvise.
            _ => Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' surface {descriptor.Surface} has no op leaf in V1"),
        };
    }

    // Registry → SetRegistryValueOp with the descriptor's exact (hive, path, name, kind)
    // and a value VALIDATED for the declared kind. The op carries the canonical string
    // form (re-serialized) so it byte-matches what the probe reads.
    private static Result<PrivilegedOp> MapRegistry(
        string tweakId, ReconcilableDescriptor descriptor, string desiredValueJson)
    {
        // A Registry-surface descriptor must carry its registry location (closed table
        // invariant); guard defensively rather than dereference a null.
        if (descriptor.Registry is not { } reg)
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' is Registry-surface but carries no registry location");

        var canonical = CanonicalizeRegistryValue(reg.Kind, desiredValueJson);
        if (!canonical.IsOk)
            return Result<PrivilegedOp>.Fail(canonical.Error!.Value.Code, canonical.Error.Value.Message);

        return Result<PrivilegedOp>.Ok(
            new SetRegistryValueOp(reg.Hive, reg.Path, reg.Name, reg.Kind, canonical.Value!));
    }

    // Validate the desired value parses for the declared kind, then RE-SERIALIZE it
    // through the canonicalizer so leading zeros / formatting are normalized to the
    // exact string the probe reads (ordinal equality with the manifest).
    private static Result<string> CanonicalizeRegistryValue(RegistryValueType kind, string desiredValueJson)
        => kind switch
        {
            RegistryValueType.DWord =>
                uint.TryParse(desiredValueJson, NumberStyles.None, CultureInfo.InvariantCulture, out var dword)
                    ? Result<string>.Ok(ReconcilableValueCanonicalizer.Dword(dword))
                    : Result<string>.Fail(
                        ReconcilerErrorCodes.TweakNotAllowlisted,
                        $"value '{desiredValueJson}' is not a valid DWord"),

            RegistryValueType.QWord =>
                ulong.TryParse(desiredValueJson, NumberStyles.None, CultureInfo.InvariantCulture, out var qword)
                    ? Result<string>.Ok(ReconcilableValueCanonicalizer.Qword(qword))
                    : Result<string>.Fail(
                        ReconcilerErrorCodes.TweakNotAllowlisted,
                        $"value '{desiredValueJson}' is not a valid QWord"),

            // A String value has no numeric shape to validate; carry it verbatim.
            RegistryValueType.String =>
                Result<string>.Ok(ReconcilableValueCanonicalizer.String(desiredValueJson)),

            _ => Result<string>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"unsupported registry value kind {kind}"),
        };

    // PowerPlan → SetActivePowerPlanOp. The desired value is the canonical 'D' GUID the
    // canonicalizer emits; anything that is not a GUID → Fail (the helper allowlist still
    // gates which plan GUIDs are accepted, so an in-range GUID is bounded downstream).
    private static Result<PrivilegedOp> MapPowerPlan(string tweakId, string desiredValueJson)
        => Guid.TryParse(desiredValueJson, out var guid)
            ? Result<PrivilegedOp>.Ok(new SetActivePowerPlanOp(guid))
            : Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"value '{desiredValueJson}' is not a valid power-plan GUID for tweak '{tweakId}'");

    // Service → RunServiceControlOp on the descriptor's service name. Only the run/stop
    // tokens map to an action; "notinstalled" / "unknown" / anything else → Fail. The
    // reconciler never installs/uninstalls a service as drift-repair, so ServiceAction
    // .Install is never emitted here (install is an apply-time concern).
    private static Result<PrivilegedOp> MapService(
        string tweakId, ReconcilableDescriptor descriptor, string desiredValueJson)
    {
        if (string.IsNullOrEmpty(descriptor.ServiceName))
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' is Service-surface but carries no service name");

        return desiredValueJson switch
        {
            "running" => Result<PrivilegedOp>.Ok(
                new RunServiceControlOp(descriptor.ServiceName, ServiceAction.Start)),
            "stopped" => Result<PrivilegedOp>.Ok(
                new RunServiceControlOp(descriptor.ServiceName, ServiceAction.Stop)),

            // "notinstalled" / "unknown" / anything else: the reconciler does not (un)install.
            _ => Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"desired service state '{desiredValueJson}' is not re-appliable for tweak '{tweakId}'"),
        };
    }
}
