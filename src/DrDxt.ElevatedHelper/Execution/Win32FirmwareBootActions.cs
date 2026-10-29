using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32FirmwareBootActions : le voyage firmware et son annulation, par shutdown.exe (`/r /fw` envoie le prochain
// démarrage au setup firmware et redémarre, `/a /fw` retire la demande). Windows possède le bit OsIndications ; DrDXT
// n'écrit aucune variable firmware (la spécification UEFI, pas la NVRAM à la main : compte pour un antivirus comme pour
// la règle qui interdit d'écrire le varstore `Setup`).
//   * Arguments construits d'un entier borné, jamais d'une chaîne de l'appelant ; aucune commande quelconque.
//   * Codes de sortie lus : un firmware sans démarrage vers le setup rend non nul, remonté comme un échec.
// -----------------------------------------------------------------------
public sealed class Win32FirmwareBootActions : IFirmwareBootActions
{
    // Depuis le dossier système, jamais le PATH : un shutdown.exe du dossier courant tournerait en élevé.
    private static string ShutdownExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");

    // shutdown.exe rend la main tout de suite (il planifie) : ce délai ne garde qu'un processus bloqué.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public Result RequestFirmwareSetupReboot(int delaySeconds)
    {
        if (delaySeconds < 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "A negative reboot delay is not a delay.");

        // /r redémarrage · /fw prochain démarrage dans le setup firmware · /t <n> délai.
        return Run($"/r /fw /t {delaySeconds}",
                   "Windows refused the firmware-setup reboot request "
                   + "(the firmware may not support boot-to-setup).");
    }

    public Result CancelPendingFirmwareBoot()
        // /a annule un arrêt en attente, /fw efface avec lui le démarrage firmware en attente. Rien en attente rend 1116
        // (ERROR_NO_SHUTDOWN_IN_PROGRESS) : l'état demandé par l'appelant. Tout autre code non nul est un refus, et il est dit.
        => Run("/a /fw", "Windows did not withdraw the pending firmware-setup reboot.", AnnulationAboutie);

    internal static bool AnnulationAboutie(int code) => code is 0 or 1116;

    private static Result Run(string arguments, string failureMessage, Func<int, bool>? aboutie = null)
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

            var r = ProcessusBorne.Executer(psi, CallTimeout);
            if (!r.Lance)
                return Result.Fail(ErrorCodes.HelperOpFailed, "shutdown.exe could not be started.");
            if (!r.Fini)
                return Result.Fail(ErrorCodes.HelperOpFailed, "shutdown.exe did not return in time.");

            // Une sortie en échec est dite, jamais avalée en succès silencieux.
            if (!(aboutie ?? (c => c == 0))(r.Code))
                return Result.Fail(ErrorCodes.HelperOpFailed, failureMessage);

            return Result.Ok();
        }
        catch
        {
            // Échec propre : aucune exception ne traverse l'IPC, aucun détail de l'hôte ne fuit.
            return Result.Fail(ErrorCodes.HelperOpFailed, "The firmware-boot request failed.");
        }
    }

    // ── lecture firmware ─────────────────────────────────────────────────────────
    // Exprès aucun P/Invoke Set* : une variable ecrite au mauvais offset peut rendre une carte non demarrable, sans autre
    // recours que physique. La lecture dit, au retour du BIOS, si le client a fait le reglage, au lieu de l'inferer.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFirmwareEnvironmentVariableExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttributes);

    // 1314 = ERROR_PRIVILEGE_NOT_HELD : « nous n'avions pas le privilege », non concluant, pas « le firmware a refuse ».
    private const int ErrorPrivilegeNotHeld = 1314;

    // 203 = ERROR_ENVVAR_NOT_FOUND : une absence, pas un refus (mesure : « Setup » rend 203 sous le GUID EFI global, le
    // varstore du fabricant a son propre GUID).
    private const int ErrorEnvVarNotFound = 203;

    // 122 = ERROR_INSUFFICIENT_BUFFER : la variable existe, plus grande que notre tampon ; notre limite, ni refus ni
    // absence.
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Taille du tampon de lecture, alignee sur PrivilegedResponse.MaxDataBytes : plus, une lecture reussie echouerait au
    /// transport. 1 Ko ne suffit pas : le varstore de configuration d'une carte pese plusieurs Ko.
    /// </summary>
    private const int ReadBufferBytes = 16 * 1024;

    /// <summary>Lit une variable firmware EFI. Fail-clean : aucune exception ne traverse l'IPC.</summary>
    public Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(vendorGuid))
            return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "Firmware variable name and GUID are required.");

        try
        {
            // Etre admin ne suffit pas (mesure) : SeSystemEnvironmentPrivilege est present mais desactive en session
            // elevee, et l'appel rend 1314 sans cette activation.
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
                    // 122 = notre tampon est trop petit : la variable existe et est lisible.
                    ErrorInsufficientBuffer =>
                        $"the variable exists but is larger than the {ReadBufferBytes}-byte read buffer",
                    // 1314 = le privilege n'est pas actif (peut-etre dans le token, desactive) : non concluant.
                    ErrorPrivilegeNotHeld => "SeSystemEnvironmentPrivilege is not ACTIVE -- INCONCLUSIVE, not a firmware refusal",
                    // 203 = cette variable n'existe pas sous ce GUID.
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
