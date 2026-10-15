using System.Runtime.Versioning;
using System.ServiceProcess;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Win32ServiceStateReader : le vrai IServiceStateReader (lecture seule).
//
// Lit l'état d'un service via System.ServiceProcess.ServiceController. Un service non
// installé → Ok(ServiceState.NotInstalled). Une vraie erreur de requête →
// Fail(TWEAK.SDK_UNAVAILABLE). Ne lève jamais. Démarrer et arrêter sont privilégiés et
// passent par le helper (RunServiceControlOp).
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32ServiceStateReader : IServiceStateReader
{
    public Result<ServiceState> GetServiceState(string serviceName)
    {
        try
        {
            var exists = ServiceController.GetServices().Any(s =>
                string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                return Result<ServiceState>.Ok(ServiceState.NotInstalled);

            using var sc = new ServiceController(serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                    => Result<ServiceState>.Ok(ServiceState.Running),
                _ => Result<ServiceState>.Ok(ServiceState.Stopped),
            };
        }
        catch (Exception)
        {
            return Result<ServiceState>.Fail(ErrorCodes.TweakSdkUnavailable, "Service-state read failed.");
        }
    }
}
