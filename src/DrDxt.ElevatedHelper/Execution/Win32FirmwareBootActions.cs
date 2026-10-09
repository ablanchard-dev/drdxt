using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32FirmwareBootActions — M3 lot 2, the firmware trip and its undo.
//
// Both are shutdown.exe: `/r /fw` asks Windows to send the NEXT boot into the firmware
// setup UI and restart, `/a /fw` withdraws that request. Windows owns the OsIndications
// bit; DrDXT writes NO firmware variable of its own. That is deliberate — it is the
// difference between using the UEFI specification and hand-rolling NVRAM, and it matters
// as much to an antivirus scanning the binary as it does to the product rule that forbids
// writing the `Setup` varstore (docs/M3-BIOS-ecriture-preuves-2026-08-25.md).
//
// The arguments are BUILT here from a bounded integer — never taken from a string the
// caller supplies. There is no path through this file that runs an arbitrary command.
//
// Exit codes are read, not assumed: shutdown.exe returns non-zero when the firmware does
// not support a boot-to-setup request, and that must surface as an honest failure rather
// than a restart the user never gets an explanation for.
// -----------------------------------------------------------------------
public sealed class Win32FirmwareBootActions : IFirmwareBootActions
{
    // shutdown.exe is resolved from the system directory, never from PATH: a PATH lookup
    // would let a shutdown.exe planted in the working directory run elevated.
    private static string ShutdownExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");

    // The window a shutdown.exe call is given to report back. It returns immediately — it
    // schedules the restart, it does not wait for it — so this only guards a hung process.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public Result RequestFirmwareSetupReboot(int delaySeconds)
    {
        if (delaySeconds < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "A negative reboot delay is not a delay.");

        // /r restart · /fw next boot into firmware setup · /t <n> delay.
        return Run($"/r /fw /t {delaySeconds}",
                   "Windows refused the firmware-setup reboot request "
                   + "(the firmware may not support boot-to-setup).");
    }

    public Result CancelPendingFirmwareBoot()
        // /a aborts a pending shutdown, /fw clears the pending firmware boot with it. Calling
        // this with nothing pending returns non-zero, and that is not an error worth surfacing:
        // the caller asked for "no pending firmware boot", and that is the state either way.
        => Run("/a /fw", null);

    private static Result Run(string arguments, string? failureMessage)
    {
        try
        {
            var psi = new ProcessStartInfo(ShutdownExe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
                return Result.Fail(ErrorCodes.HelperOpFailed, "shutdown.exe could not be started.");

            if (!p.WaitForExit((int)CallTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return Result.Fail(ErrorCodes.HelperOpFailed, "shutdown.exe did not return in time.");
            }

            // failureMessage == null means "a non-zero exit is not a failure here" (the cancel
            // case). Otherwise a non-zero exit is SAID, never swallowed into a silent success.
            if (p.ExitCode != 0 && failureMessage is not null)
                return Result.Fail(ErrorCodes.HelperOpFailed, failureMessage);

            return Result.Ok();
        }
        catch
        {
            // Fail-clean: no exception crosses the IPC, and no detail of the host leaks.
            return Result.Fail(ErrorCodes.HelperOpFailed, "The firmware-boot request failed.");
        }
    }

    // ── lecture firmware (M3) ─────────────────────────────────────────────────────────
    // Il n'existe volontairement aucun P/Invoke Set* dans ce fichier. Ecrire une variable
    // au mauvais offset peut rendre une carte non demarrable, sans annulation possible : le
    // seul recours serait physique. On ne construit donc pas la capacite.
    //
    // Ce que la lecture apporte : aujourd'hui, au retour d'un redemarrage dans le BIOS, l'app
    // ne sait pas si le client a fait le reglage -- elle l'infere. Lire la variable met fin a
    // ce silence.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFirmwareEnvironmentVariableExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttributes);

    // 1314 = ERROR_PRIVILEGE_NOT_HELD. Le distinguer compte : c'est « nous n'avions pas le
    // privilege », pas « le firmware a refuse ». Confondre les deux, c'est transformer un non
    // concluant en verdict de fond -- une erreur deja payee sur ce projet.
    private const int ErrorPrivilegeNotHeld = 1314;

    // 203 = ERROR_ENVVAR_NOT_FOUND. Mesure le 27/08 : « Setup » rend 203 sous le GUID EFI
    // GLOBAL -- la variable n'y est pas, le varstore du fabricant a son propre GUID. C'est un
    // constat d'absence, pas un refus : ne pas melanger les deux dans un message d'erreur.
    private const int ErrorEnvVarNotFound = 203;

    // 122 = ERROR_INSUFFICIENT_BUFFER. Le distinguer aussi : « la variable existe mais elle est
    // plus grande que mon tampon » n'est ni un refus, ni une absence -- c'est notre limite a
    // nous, et la confondre avec les deux autres ferait accuser le firmware de notre defaut.
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Taille du tampon de lecture. Alignee sur la borne du canal de retour de donnees
    /// (PrivilegedResponse.MaxDataBytes) : lire plus que ce que la reponse peut porter
    /// produirait une lecture reussie qui echouerait ensuite au transport, ce qui est le pire
    /// des deux mondes.
    ///
    /// 1 Ko ne suffisait pas. Le varstore de configuration d'une carte pese plusieurs Ko, donc
    /// la seule variable pour laquelle L3.1 existe aurait ete refusee par notre propre tampon --
    /// un mur honnete, et parfaitement inutile.
    /// </summary>
    private const int ReadBufferBytes = 16 * 1024;

    /// <summary>Lit une variable firmware EFI. Fail-clean : aucune exception ne traverse l'IPC.</summary>
    public Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(vendorGuid))
            return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "Firmware variable name and GUID are required.");

        try
        {
            // Etre admin ne suffit pas. Mesure du 27/08 : dans une session ELEVEE, le privilege
            // SeSystemEnvironmentPrivilege est PRESENT mais **Desactive**, et l'appel ci-dessous
            // rend 1314. Il faut l'activer d'abord -- sans cette ligne, cette methode echouait
            // en production meme lancee en administrateur.
            var privilege = FirmwarePrivilege.Enable();
            if (!privilege.IsOk)
                return Result<byte[]>.Fail(privilege.Error!.Value.Code, privilege.Error.Value.Message);

            var buffer = new byte[ReadBufferBytes];
            uint written = GetFirmwareEnvironmentVariableExW(name, vendorGuid, buffer, (uint)buffer.Length, out _);
            if (written == 0)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                string why = err switch
                {
                    // 122 = notre tampon est trop petit. C'est un constat sur NOUS, pas sur le
                    // firmware : la variable existe et est lisible.
                    ErrorInsufficientBuffer =>
                        $"the variable exists but is larger than the {ReadBufferBytes}-byte read buffer",
                    // 1314 = le privilege n'est pas actif (et non « absent » : il peut etre dans le
                    // token, simplement desactive). INCONCLUANT -- jamais un refus du firmware.
                    ErrorPrivilegeNotHeld => "SeSystemEnvironmentPrivilege is not ACTIVE -- INCONCLUSIVE, not a firmware refusal",
                    // 203 = cette variable n'existe pas sous ce GUID. Mesure : « Setup » rend 203
                    // sous le GUID EFI global -- le varstore du fabricant vit sous un GUID propre.
                    ErrorEnvVarNotFound => "no such variable under this GUID (a vendor varstore lives under its own GUID)",
                    _ => "the firmware did not return this variable",
                };
                return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed,
                    $"Firmware variable '{name}' could not be read (win32={err}): {why}.");
            }

            if (written > (uint)buffer.Length)
                return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed,
                    $"Firmware variable '{name}' reported {written} bytes into a {ReadBufferBytes}-byte "
                    + "buffer, which cannot have happened -- the read is not trusted.");

            return Result<byte[]>.Ok(buffer[..(int)written]);
        }
        catch
        {
            // Fail-clean, comme le reste du fichier : rien du host ne fuit par l'IPC.
            return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "The firmware variable read failed.");
        }
    }
}
