using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// WmiRestorePointService — the real IRestorePointService.
//
// * IsEnabled(): System Restore is usable when the SR config reports a non-zero
//   disk-space allocation for the system drive (queried via WMI
//   root\default:SystemRestoreConfig, with a registry fallback). Read-only.
// * TryEnable(): enabling System Restore IS a privileged write, so it is routed
//   through the elevated helper via a typed EnableSystemRestoreOp (system drive
//   only, allowlist leaf-exact). After the helper enables it we RE-CHECK
//   IsEnabled() and return that — true only if restore is genuinely usable now.
//   The SafetyManager already asks consent (IConsentPrompt.AskEnableRestore)
//   before calling this; here we make the enable actually happen.
// * VerifyPointExists(desc): queries recent points (root\default:SystemRestore)
//   for a matching description — the guard against a silent no-op create.
//
// The CREATE itself flows through the elevated helper (CreateRestorePointOp);
// this service answers "is restore usable", drives the helper enable, and
// verifies "did the point land". Every path is wrapped; nothing throws.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class WmiRestorePointService : IRestorePointService
{
    private const string SystemRestoreScope = @"\\.\root\default";

    private readonly IElevatedClient _elevated;

    public WmiRestorePointService(IElevatedClient elevated) =>
        _elevated = elevated ?? throw new ArgumentNullException(nameof(elevated));

    public bool IsEnabled()
    {
        try
        {
            // Primary: WMI SystemRestoreConfig (RPSessionInterval > 0 means SR is on).
            using var searcher = new ManagementObjectSearcher(
                SystemRestoreScope, "SELECT RPSessionInterval FROM SystemRestoreConfig");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    var raw = o["RPSessionInterval"];
                    if (raw is not null && int.TryParse(raw.ToString(), out var interval) && interval > 0)
                        return true;
                }
            }
        }
        catch
        {
            // fall through to the registry probe
        }

        // Fallback: the SR config registry key (RPSessionInterval).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: false);
            var value = key?.GetValue("RPSessionInterval");
            return value is int i && i > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryEnable()
    {
        // Already usable? Nothing to do.
        if (IsEnabled())
            return true;

        try
        {
            // Enabling System Restore is a privileged write — route it through the
            // elevated helper with a typed, allowlisted EnableSystemRestoreOp on the
            // system drive only. Then re-check: return true only if SR is genuinely
            // usable now (GPO/SKU can still no-op the enable → we report not-usable).
            var drive = SystemDrive();
            var result = _elevated
                .ExecuteAsync(new EnableSystemRestoreOp(drive), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!result.IsOk)
                return false;

            return IsEnabled();
        }
        catch
        {
            // Never throw across this boundary — a failed enable just reports off.
            return false;
        }
    }

    // The system drive in "X:" form (e.g. "C:"). %SystemDrive% is already "C:"; we
    // trim any trailing slash. Falls back to "C:" if the environment is unreadable.
    private static string SystemDrive()
    {
        try
        {
            var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (!string.IsNullOrWhiteSpace(sysDrive))
                return sysDrive.TrimEnd('\\', '/');
        }
        catch
        {
            // fall through to the default
        }
        return "C:";
    }

    public DateTime? DernierPointDepuis(DateTime depuisUtc)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                SystemRestoreScope, "SELECT CreationTime FROM SystemRestore");
            var creations = new List<string?>();
            foreach (var o in searcher.Get())
            {
                using (o) creations.Add(o["CreationTime"]?.ToString());
            }
            return PlusRecentDepuis(creations, depuisUtc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Le point le plus recent cree depuis <paramref name="depuisUtc"/>, en UTC, ou <c>null</c>.
    /// Les dates sont celles de la classe WMI SystemRestore (ex. « 20260911222955.512634-000 », lue sur le
    /// poste le 14/09/2026). Une date illisible est ignoree : un point dont on ne sait pas l'age ne
    /// prouve pas qu'il protege ce changement.</summary>
    internal static DateTime? PlusRecentDepuis(IEnumerable<string?> creationsDmtf, DateTime depuisUtc)
    {
        DateTime? plusRecent = null;
        foreach (var dmtf in creationsDmtf)
        {
            if (string.IsNullOrWhiteSpace(dmtf)) continue;
            DateTime utc;
            try { utc = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime(); }
            catch { continue; }
            if (utc >= depuisUtc && (plusRecent is null || utc > plusRecent)) plusRecent = utc;
        }
        return plusRecent;
    }
}
