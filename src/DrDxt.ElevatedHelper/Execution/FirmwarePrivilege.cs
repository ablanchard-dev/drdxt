using System.Runtime.InteropServices;
using DrDxt.Core;
using static DrDxt.ElevatedHelper.Natif;

namespace DrDxt.ElevatedHelper.Execution;

// FirmwarePrivilege : activer SeSystemEnvironmentPrivilege avant toute lecture firmware. Administrateur ne suffit pas
// (mesure le 27/08/2026 sur la machine de reference) : en session elevee, `whoami /priv` montre le privilege
// present mais desactive, et GetFirmwareEnvironmentVariableExW rend `win32=1314` (« privilege pas active », pas « le
// firmware a refuse »). Windows le livre dans le token sans l'activer.
// Piege d'alignement : un LUID declare en `long` est aligne sur 8 octets, TOKEN_PRIVILEGES pese 24 au lieu de 16, et
// AdjustTokenPrivileges rend ERROR_NOT_ALL_ASSIGNED (1300) a un administrateur. D'ou LowPart/HighPart separes et
// Pack = 1 ; `TokenPrivilegesSize` laisse un test verrouiller les 16 octets. Mesure : 16 octets, AdjustTokenPrivileges
// = succes, puis BootOrder/BootCurrent/SecureBoot lues pour de vrai.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FirmwarePrivilege
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>Taille attendue de TOKEN_PRIVILEGES : <b>16</b> octets. Exposee pour que le
    /// test puisse la verrouiller : 24 signifierait que le piege d'alignement est revenu.</summary>
    public static int TokenPrivilegesSize => Marshal.SizeOf<TokenPrivileges>();

    private const string SeSystemEnvironmentPrivilege = "SeSystemEnvironmentPrivilege";
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Active SeSystemEnvironmentPrivilege sur le process courant ; ne jette jamais. Un echec dit son motif :
    /// ERROR_NOT_ALL_ASSIGNED (1300) = privilege absent du token (session non elevee), jamais un refus du firmware.</summary>
    public static Result Enable()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    $"Could not open the process token (win32={Marshal.GetLastWin32Error()}).");

            if (!LookupPrivilegeValueW(null, SeSystemEnvironmentPrivilege, out var luid))
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    $"Could not look up {SeSystemEnvironmentPrivilege} (win32={Marshal.GetLastWin32Error()}).");

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            // AdjustTokenPrivileges rend true meme quand il n'a rien pu assigner : le vrai
            // verdict est dans GetLastWin32Error. Le tester serait un faux positif.
            bool called = AdjustTokenPrivileges(
                token, false, ref tp, (uint)TokenPrivilegesSize, IntPtr.Zero, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();

            if (!called)
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    $"AdjustTokenPrivileges failed (win32={err}).");

            if (err == ErrorNotAllAssigned)
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    $"{SeSystemEnvironmentPrivilege} is not in this process token (win32=1300): " +
                    "the session is not elevated. This says nothing about what the firmware would allow.");

            if (err != 0)
                return Result.Fail(ErrorCodes.HelperOpFailed,
                    $"{SeSystemEnvironmentPrivilege} could not be enabled (win32={err}).");

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Enabling the firmware privilege failed: {ex.Message}");
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
