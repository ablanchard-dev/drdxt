using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Core;

/// <summary>
/// Le plan d'alimentation actif, lu en un seul endroit (PowerGetActiveScheme, powrprof.dll,
/// sans elevation). L'appli, l'audit, le helper et le service de demarrage lui deleguent et gardent chacun leurs codes
/// d'erreur. Le tampon rendu par Windows est libere par LocalFree dans tous les cas. Ne leve jamais.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlanActif
{
    /// <summary>Le GUID du plan actif, ou ce qui l'a empeche : code de retour Win32, ou message d'exception.</summary>
    public static (Guid? Plan, uint CodeRetour, string? Exception) Lire()
    {
        var tampon = IntPtr.Zero;
        try
        {
            var rc = PowerGetActiveScheme(IntPtr.Zero, out tampon);
            return rc != 0 || tampon == IntPtr.Zero
                ? (null, rc, null)
                : (Marshal.PtrToStructure<Guid>(tampon), 0, null);
        }
        catch (Exception ex)
        {
            return (null, 0, ex.Message);
        }
        finally
        {
            if (tampon != IntPtr.Zero) LocalFree(tampon);
        }
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
