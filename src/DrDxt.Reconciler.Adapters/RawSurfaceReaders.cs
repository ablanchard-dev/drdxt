using System.Globalization;
using System.ServiceProcess;
using DrDxt.Core;
using DrDxt.Reconciler;
using Microsoft.Win32;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// The three RAW OS read seams behind SurfaceSettingProbe, plus their thin real
// implementations. The probe's dispatch + canonical serialization are unit-tested
// against fakes of these seams; the real readers below are the only untested edges
// (each just touches one OS boundary and wraps any throw in a Result).
//
// Each reader returns the value already CANONICALIZED (via
// ReconcilableValueCanonicalizer) so the probe never has to know the surface's raw
// representation — and the value byte-matches the manifest's DesiredValueJson.
// -----------------------------------------------------------------------

/// <summary>Reads ONE registry value described by a <see cref="RegistryDescriptor"/>
/// and returns its canonical string. Missing key/value or read error → Fail.</summary>
internal interface IRawRegistryValueReader
{
    Result<string> ReadCanonical(RegistryDescriptor descriptor);
}

/// <summary>Reads the active power scheme GUID and returns it canonicalized. Read
/// error → Fail.</summary>
internal interface IRawPowerPlanReader
{
    Result<string> ReadActiveSchemeCanonical();
}

/// <summary>Reads a named service's run state and returns the canonical token. A
/// not-installed service is a VALID state ("notinstalled"); a genuine query error →
/// Fail.</summary>
internal interface IRawServiceStateReader
{
    Result<string> ReadStateCanonical(string serviceName);
}

// -----------------------------------------------------------------------
// Real implementations (untested edges).
// -----------------------------------------------------------------------

/// <summary>The thin real registry value reader. Opens the hive sub-key read-only,
/// reads the named value, and canonicalizes it per the descriptor's kind. A missing
/// key/value or any boundary throw → Fail(RegistryFail).</summary>
internal sealed class Win32RegistryValueReader : IRawRegistryValueReader
{
    public Result<string> ReadCanonical(RegistryDescriptor descriptor)
    {
        try
        {
            var root = descriptor.Hive switch
            {
                RegistryHiveKind.LocalMachine => Registry.LocalMachine,
                RegistryHiveKind.CurrentUser => Registry.CurrentUser,
                _ => null,
            };
            if (root is null)
                return Result<string>.Fail(ErrorCodes.RegistryFail,
                    $"Unsupported registry hive: {descriptor.Hive}.");

            using var key = root.OpenSubKey(descriptor.Path, writable: false);
            if (key is null)
                return Result<string>.Fail(ErrorCodes.RegistryFail,
                    $"Registry key not found: {descriptor.Path}.");

            var raw = key.GetValue(descriptor.Name);
            if (raw is null)
                return Result<string>.Fail(ErrorCodes.RegistryFail,
                    $"Registry value not found: {descriptor.Name}.");

            return descriptor.Kind switch
            {
                RegistryValueType.DWord when TryToUInt(raw, out var dw)
                    => Result<string>.Ok(ReconcilableValueCanonicalizer.Dword(dw)),
                RegistryValueType.QWord when TryToULong(raw, out var qw)
                    => Result<string>.Ok(ReconcilableValueCanonicalizer.Qword(qw)),
                RegistryValueType.String
                    => Result<string>.Ok(ReconcilableValueCanonicalizer.String(
                        raw.ToString() ?? string.Empty)),
                _ => Result<string>.Fail(ErrorCodes.RegistryFail,
                    $"Registry value '{descriptor.Name}' is not a {descriptor.Kind}."),
            };
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ErrorCodes.RegistryFail, ex.Message);
        }
    }

    private static bool TryToUInt(object raw, out uint value)
    {
        switch (raw)
        {
            case int i: value = unchecked((uint)i); return true;
            case uint u: value = u; return true;
            case long l: value = unchecked((uint)l); return true;
            default:
                value = 0;
                return raw is string s && uint.TryParse(s, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }
    }

    private static bool TryToULong(object raw, out ulong value)
    {
        switch (raw)
        {
            case long l: value = unchecked((ulong)l); return true;
            case ulong ul: value = ul; return true;
            case int i: value = unchecked((ulong)i); return true;
            default:
                value = 0;
                return raw is string s && ulong.TryParse(s, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }
    }
}

/// <summary>The active-power-plan reader : the native call lives in PlanActif (Core).
/// Any non-zero return / boundary throw → Fail(PowerFail).</summary>
internal sealed class PInvokePowerPlanReader : IRawPowerPlanReader
{
    public Result<string> ReadActiveSchemeCanonical()
    {
        var (plan, rc, exception) = PlanActif.Lire();
        return plan is { } guid
            ? Result<string>.Ok(ReconcilableValueCanonicalizer.PowerPlan(guid))
            : Result<string>.Fail(ErrorCodes.PowerFail, exception ?? $"PowerGetActiveScheme failed (rc={rc}).");
    }
}

/// <summary>The thin real service-state reader via System.ServiceProcess. A service
/// that is not installed → "notinstalled" (a valid state, not an error). Any other
/// boundary throw → Fail(IoFailure).</summary>
internal sealed class ServiceControllerStateReader : IRawServiceStateReader
{
    public Result<string> ReadStateCanonical(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            var status = controller.Status; // throws if the service is not installed
            var state = status switch
            {
                ServiceControllerStatus.Running
                    or ServiceControllerStatus.StartPending
                    or ServiceControllerStatus.ContinuePending => ServiceRunState.Running,
                _ => ServiceRunState.Stopped,
            };
            return Result<string>.Ok(ReconcilableValueCanonicalizer.Service(state));
        }
        catch (InvalidOperationException)
        {
            // Not installed — a valid, comparable state, not a probe failure.
            return Result<string>.Ok(
                ReconcilableValueCanonicalizer.Service(ServiceRunState.NotInstalled));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }
}
