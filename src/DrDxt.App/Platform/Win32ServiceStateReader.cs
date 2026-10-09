using System.Runtime.Versioning;
using System.ServiceProcess;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Win32ServiceStateReader — the real IServiceStateReader (read-only).
//
// Reads a service's state via System.ServiceProcess.ServiceController. A service
// that is not installed → Ok(ServiceState.NotInstalled). A genuine query error →
// Fail(TWEAK.SDK_UNAVAILABLE). Never throws. Start/stop are privileged and go
// through the helper (RunServiceControlOp).
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
