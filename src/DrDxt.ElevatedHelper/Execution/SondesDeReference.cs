using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// 11/09/2026 — les deux sondes de la reference de derive, lues par le helper eleve au moment du
// CommitManifestOp (MachineManifestCommit → ApplyReferenceSeedingStore). Lecture seule.
// Simplification voulue : memes lectures que RegistryOsBuildProbe / WmiGpuDriverProbe (DrDxt.Reconciler.Adapters), qui
// tirent Detection ; le helper eleve reste sans Detection. Meme jeton, meme regle d'absence.
// -----------------------------------------------------------------------

/// <summary>« {CurrentBuild}.{UBR} » depuis HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion.</summary>
[SupportedOSPlatform("windows")]
public sealed class SondeDeBuildWin32 : IOsBuildProbe
{
    public Result<string> ReadCurrentBuild()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            var build = cle?.GetValue("CurrentBuild") as string;
            var ubr = cle?.GetValue("UBR") is int u ? unchecked((uint)u) : (uint?)null;
            return string.IsNullOrWhiteSpace(build) || ubr is null
                ? Result<string>.Fail(ErrorCodes.RegistryFail, "CurrentBuild or UBR is missing.")
                : Result<string>.Ok($"{build.Trim()}.{ubr.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ErrorCodes.RegistryFail, ex.Message);
        }
    }
}

/// <summary>La DriverVersion de l'adaptateur dont le fabricant correspond ; <c>Ok(null)</c> = absent.</summary>
[SupportedOSPlatform("windows")]
public sealed class SondeDePiloteWmi : IGpuDriverProbe
{
    public Result<string?> ReadDriverVersion(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return Result<string?>.Ok(null);
        try
        {
            using var requete = new ManagementObjectSearcher(
                "SELECT DriverVersion, AdapterCompatibility, Name FROM Win32_VideoController");
            foreach (var ligne in requete.Get())
            {
                using (ligne)
                {
                    var fabricant = ligne["AdapterCompatibility"] as string;
                    var nom = ligne["Name"] as string;
                    if (fabricant?.Contains(vendor, StringComparison.OrdinalIgnoreCase) != true
                        && nom?.Contains(vendor, StringComparison.OrdinalIgnoreCase) != true)
                        continue;
                    var version = ligne["DriverVersion"] as string;
                    return Result<string?>.Ok(string.IsNullOrWhiteSpace(version) ? null : version);
                }
            }
            return Result<string?>.Ok(null);
        }
        catch (Exception ex)
        {
            return Result<string?>.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }
}
