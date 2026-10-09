using DrDxt.Core;
using DrDxt.Detection.Wmi;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// WmiGpuDriverProbe — IGpuDriverProbe over the existing IWmiQuery seam.
//
// Queries Win32_VideoController and returns the DriverVersion of the adapter whose
// vendor matches (case-insensitive substring on AdapterCompatibility or Name). A
// per-vendor DriverVersion change since apply time is the driver-update drift signal
// (plan T8b/T8c).
//
// HONESTY of absence: no matching adapter (or a matching adapter with no usable
// DriverVersion) → Ok(null) — the vendor simply isn't present; that is not an error.
// Only a genuine WMI query failure → Fail. The WMI boundary is the existing
// ManagementWmiQuery, which already wraps every System.Management throw in a Result.
// -----------------------------------------------------------------------
public sealed class WmiGpuDriverProbe : IGpuDriverProbe
{
    private const string VideoControllerQuery =
        "SELECT DriverVersion, AdapterCompatibility, Name FROM Win32_VideoController";

    private readonly IWmiQuery _wmi;

    public WmiGpuDriverProbe(IWmiQuery wmi) => _wmi = wmi;

    public Result<string?> ReadDriverVersion(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return Result<string?>.Ok(null);

        var rows = _wmi.Query(VideoControllerQuery);
        if (!rows.IsOk)
            return Result<string?>.Fail(rows.Error!.Value.Code, rows.Error.Value.Message);

        foreach (var row in rows.Value!)
        {
            var compat = row.GetString("AdapterCompatibility");
            var name = row.GetString("Name");

            var matches =
                (compat is not null && compat.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                || (name is not null && name.Contains(vendor, StringComparison.OrdinalIgnoreCase));

            if (!matches)
                continue;

            var driverVersion = row.GetString("DriverVersion");
            // A matching adapter with no usable version is treated as "absent" (Ok(null)),
            // not an error — the drift signal simply has no value to compare this tick.
            return Result<string?>.Ok(
                string.IsNullOrWhiteSpace(driverVersion) ? null : driverVersion);
        }

        // No adapter for this vendor — absence is not an error.
        return Result<string?>.Ok(null);
    }
}
