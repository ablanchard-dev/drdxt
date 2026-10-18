using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32FirmwareBootActions : le voyage firmware et son annulation.
//
// Les deux sont shutdown.exe : `/r /fw` demande à Windows d'envoyer le prochain démarrage dans
// l'interface de setup du firmware et redémarre, `/a /fw` retire cette demande. Windows possède
// le bit OsIndications ; DrDXT n'écrit aucune variable firmware lui-même. C'est voulu : c'est la
// différence entre utiliser la spécification UEFI et bricoler la NVRAM à la main, et cela compte
// autant pour un antivirus qui analyse le binaire que pour la règle produit qui interdit d'écrire
// le varstore `Setup`.
//
// Les arguments sont construits ici à partir d'un entier borné, jamais pris dans une chaîne
// fournie par l'appelant. Aucun chemin de ce fichier ne lance une commande quelconque.
//
// Les codes de sortie sont lus, pas supposés : shutdown.exe rend une valeur non nulle quand le
// firmware ne gère pas le démarrage vers le setup, et cela doit remonter comme un échec honnête
// plutôt qu'un redémarrage que l'utilisateur ne s'explique jamais.
// -----------------------------------------------------------------------
public sealed class Win32FirmwareBootActions : IFirmwareBootActions
{
    // shutdown.exe est résolu depuis le dossier système, jamais depuis le PATH : une recherche
    // par PATH laisserait un shutdown.exe posé dans le dossier courant tourner en élevé.
    private static string ShutdownExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");

    // Le délai laissé à un appel shutdown.exe pour répondre. Il rend la main tout de suite (il
    // planifie le redémarrage, il ne l'attend pas) : ce délai ne garde donc qu'un processus bloqué.
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
    // Il n'existe volontairement aucun P/Invoke Set* dans ce fichier. Ecrire une variable
    // au mauvais offset peut rendre une carte non demarrable, sans annulation possible : le
    // seul recours serait physique. On ne construit donc pas la capacite.
    //
    // Ce que la lecture apporte : sans elle, au retour d'un redemarrage dans le BIOS, l'app
    // ne sait pas si le client a fait le reglage : elle l'infere. Lire la variable met fin a
    // ce silence.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFirmwareEnvironmentVariableExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttributes);

    // 1314 = ERROR_PRIVILEGE_NOT_HELD. Le distinguer compte : c'est « nous n'avions pas le
    // privilege », pas « le firmware a refuse ». Confondre les deux, c'est transformer un non
    // concluant en verdict de fond.
    private const int ErrorPrivilegeNotHeld = 1314;

    // 203 = ERROR_ENVVAR_NOT_FOUND. Mesure : « Setup » rend 203 sous le GUID EFI
    // global : la variable n'y est pas, le varstore du fabricant a son propre GUID. C'est un
    // constat d'absence, pas un refus : ne pas melanger les deux dans un message d'erreur.
    private const int ErrorEnvVarNotFound = 203;

    // 122 = ERROR_INSUFFICIENT_BUFFER. Le distinguer aussi : « la variable existe mais elle est
    // plus grande que mon tampon » n'est ni un refus, ni une absence : c'est notre limite a
    // nous, et la confondre avec les deux autres ferait accuser le firmware de notre defaut.
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Taille du tampon de lecture. Alignee sur la borne du canal de retour de donnees
    /// (PrivilegedResponse.MaxDataBytes) : lire plus que ce que la reponse peut porter
    /// produirait une lecture reussie qui echouerait ensuite au transport, ce qui est le pire
    /// des deux mondes.
    ///
    /// 1 Ko ne suffit pas. Le varstore de configuration d'une carte pese plusieurs Ko, donc
    /// la seule variable pour laquelle cette lecture existe serait refusee par notre propre tampon :
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
            // Etre admin ne suffit pas. Mesure : dans une session elevee, le privilege
            // SeSystemEnvironmentPrivilege est present mais desactive, et l'appel ci-dessous
            // rend 1314. Il faut l'activer d'abord : sans cette ligne, cette methode echouerait
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
                    // 122 = notre tampon est trop petit. C'est un constat sur nous, pas sur le
                    // firmware : la variable existe et est lisible.
                    ErrorInsufficientBuffer =>
                        $"the variable exists but is larger than the {ReadBufferBytes}-byte read buffer",
                    // 1314 = le privilege n'est pas actif (et non « absent » : il peut etre dans le
                    // token, simplement desactive). Non concluant : jamais un refus du firmware.
                    ErrorPrivilegeNotHeld => "SeSystemEnvironmentPrivilege is not ACTIVE -- INCONCLUSIVE, not a firmware refusal",
                    // 203 = cette variable n'existe pas sous ce GUID. Mesure : « Setup » rend 203
                    // sous le GUID EFI global : le varstore du fabricant vit sous un GUID propre.
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
