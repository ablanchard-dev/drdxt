using System.Globalization;
using System.ServiceProcess;
using DrDxt.Core;
using DrDxt.Reconciler;
using Microsoft.Win32;

namespace DrDxt.Reconciler.Adapters;

// Les trois coutures de lecture OS brutes derrière SurfaceSettingProbe et leurs implémentations minces, seuls bords non
// testés (une frontière OS chacun, toute exception dans un Result) ; la sonde est testée avec des faux. Chaque lecteur
// rend la valeur canonicalisée (ReconcilableValueCanonicalizer), égale à l'octet près au DesiredValueJson du manifeste.

/// <summary>Lit une seule valeur de registre décrite par un <see cref="RegistryDescriptor"/> et
/// rend sa chaîne canonique. Clé ou valeur absente, ou erreur de lecture → Fail.</summary>
internal interface IRawRegistryValueReader
{
    Result<string> ReadCanonical(RegistryDescriptor descriptor);
}

/// <summary>Lit le GUID du plan d'alimentation actif et le rend canonicalisé. Erreur de
/// lecture → Fail.</summary>
internal interface IRawPowerPlanReader
{
    Result<string> ReadActiveSchemeCanonical();
}

/// <summary>Lit l'état de marche d'un service nommé et rend le jeton canonique. Un service non
/// installé est un état valide (« notinstalled ») ; une vraie erreur de requête →
/// Fail.</summary>
internal interface IRawServiceStateReader
{
    Result<string> ReadStateCanonical(string serviceName);
}

// -----------------------------------------------------------------------
// Vraies implémentations (bords non testés).
// -----------------------------------------------------------------------

/// <summary>Le vrai lecteur mince de valeur de registre. Ouvre la sous-clé de la ruche en
/// lecture seule, lit la valeur nommée et la canonicalise selon le type du descripteur. Clé ou
/// valeur absente, ou toute exception à la frontière → Fail(RegistryFail).</summary>
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

/// <summary>Le lecteur du plan d'alimentation actif : l'appel natif vit dans PlanActif (Core).
/// Tout retour non nul / toute exception à la frontière → Fail(PowerFail).</summary>
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

/// <summary>Le vrai lecteur mince d'état de service via System.ServiceProcess. Un service non
/// installé → « notinstalled » (un état valide, pas une erreur). Toute autre exception à la
/// frontière → Fail(IoFailure).</summary>
internal sealed class ServiceControllerStateReader : IRawServiceStateReader
{
    public Result<string> ReadStateCanonical(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            var status = controller.Status; // lève si le service n'est pas installé
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
            // Non installé : un état valide et comparable, pas un échec de sonde.
            return Result<string>.Ok(
                ReconcilableValueCanonicalizer.Service(ServiceRunState.NotInstalled));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }
}
