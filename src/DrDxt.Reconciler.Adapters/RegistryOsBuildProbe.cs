using System.Globalization;
using DrDxt.Core;
using DrDxt.Reconciler;
using Microsoft.Win32;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// RegistryOsBuildProbe — IOsBuildProbe over the registry.
//
// Reads HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion → CurrentBuild (string) +
// UBR (dword), returning "{CurrentBuild}.{UBR}" (e.g. "22631.3447"). A change since
// the value captured at apply time is the PRIMARY WindowsReset signal (plan §4).
//
// The raw registry read sits behind IRawOsVersionReader so the formatting/fail logic
// is fully unit-tested with a fake; the thin real reader (Win32OsVersionReader) is
// the only untested edge — it just opens the key and reads two values, wrapping any
// throw in a Result.Fail.
// -----------------------------------------------------------------------

/// <summary>Internal raw read of the two CurrentVersion values. Returns the values
/// as-read (CurrentBuild may be null/missing, UBR may be null/missing); a genuine
/// registry error → Fail. Not the place that formats or validates — that is the
/// probe, so it stays testable without the registry.</summary>
internal interface IRawOsVersionReader
{
    Result<(string? CurrentBuild, uint? Ubr)> Read();
}

/// <summary>The thin real registry reader (untested edge). Opens
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion read-only and pulls CurrentBuild
/// + UBR. Any boundary exception → Fail(RegistryFail), never thrown.</summary>
internal sealed class Win32OsVersionReader : IRawOsVersionReader
{
    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public Result<(string? CurrentBuild, uint? Ubr)> Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath, writable: false);
            if (key is null)
                return Result<(string?, uint?)>.Fail(
                    ErrorCodes.RegistryFail, $"Registry key not found: HKLM\\{CurrentVersionPath}.");

            var currentBuild = key.GetValue("CurrentBuild") as string;

            uint? ubr = key.GetValue("UBR") switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                long l => unchecked((uint)l),
                _ => null,
            };

            return Result<(string?, uint?)>.Ok((currentBuild, ubr));
        }
        catch (Exception ex)
        {
            return Result<(string?, uint?)>.Fail(ErrorCodes.RegistryFail, ex.Message);
        }
    }
}

/// <summary>Reads the Windows feature-update token "{CurrentBuild}.{UBR}".</summary>
public sealed class RegistryOsBuildProbe : IOsBuildProbe
{
    private readonly IRawOsVersionReader _reader;

    public RegistryOsBuildProbe() : this(new Win32OsVersionReader()) { }

    internal RegistryOsBuildProbe(IRawOsVersionReader reader) => _reader = reader;

    public Result<string> ReadCurrentBuild()
    {
        var read = _reader.Read();
        if (!read.IsOk)
            return Result<string>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        var (currentBuild, ubr) = read.Value;

        if (string.IsNullOrWhiteSpace(currentBuild))
            return Result<string>.Fail(ErrorCodes.RegistryFail, "CurrentBuild is missing.");
        if (ubr is null)
            return Result<string>.Fail(ErrorCodes.RegistryFail, "UBR is missing.");

        return Result<string>.Ok(
            $"{currentBuild.Trim()}.{ubr.Value.ToString(CultureInfo.InvariantCulture)}");
    }
}
