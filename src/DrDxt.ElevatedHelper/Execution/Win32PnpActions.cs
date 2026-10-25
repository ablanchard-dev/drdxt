using System.Diagnostics;
using System.Text.RegularExpressions;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32PnpActions : trois gestes du guide par pnputil (Microsoft Learn, « PnPUtil command syntax »).
//
//   /restart-device <id>                relance un périphérique dont le pilote est en échec (Windows 10 2004+) ;
//   /export-driver oemNN.inf <dossier>  puis /delete-driver oemNN.inf : un vieux paquet d'affichage, sauvegardé avant
//                                       d'être retiré (Windows 10 1607+). Jamais /force ni /uninstall : Windows refuse
//                                       alors un paquet encore utilisé par un périphérique ;
//   /add-driver <inf exporté> /install  remet ce paquet.
//
// Les arguments arrivent validés par OpAllowlist (identifiant de périphérique, nom oemNN.inf). Le chemin de l'export
// est dérivé ici du nom, jamais pris sur le fil. Le dossier des exports est durci (Administrateurs et SYSTEM) et relu
// fiable avant toute réinstallation : le helper installe en administrateur ce qu'il y trouve. pnputil.exe est résolu
// depuis le dossier système, jamais depuis le PATH. 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) est une réussite.
// -----------------------------------------------------------------------
public sealed class Win32PnpActions : IPnpActions
{
    private static string Pnputil => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    private const int RedemarrageRequis = 3010;

    private readonly Func<string[], (int Code, string Sortie)> _run;
    private readonly string _magasin;
    private readonly string _exports;
    private readonly Func<string, Result> _creerDurci;
    private readonly Func<string, Result> _fiable;
    private readonly Func<string, bool?> _enPanne;
    private readonly Func<DateTime> _maintenant;

    public Win32PnpActions() : this(Run,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDxt", "pilotes-retires"),
        TrustedManifestRoot.EnsureCreatedHardened, TrustedManifestRoot.EnsureTrustedDirectory, EnPanne, () => DateTime.UtcNow)
    { }

    internal Win32PnpActions(Func<string[], (int Code, string Sortie)> run, string magasin, string exports,
        Func<string, Result> creerDurci, Func<string, Result> fiable, Func<string, bool?> enPanne, Func<DateTime> maintenant)
        => (_run, _magasin, _exports, _creerDurci, _fiable, _enPanne, _maintenant) = (run, magasin, exports, creerDurci, fiable, enPanne, maintenant);

    public Result RestartDevice(string instanceId)
    {
        // Seul un périphérique que Windows dit en panne se relance : relancer la carte graphique, la carte réseau ou un
        // contrôleur de disque qui marchent couperait l'écran, le réseau ou un disque en pleine partie.
        if (_enPanne(instanceId) != true)
            return Result.Fail(ErrorCodes.NotAllowlisted, "This device is not reported as failing by Windows; nothing restarted.");
        var (code, _) = _run(["/restart-device", instanceId]);
        return code is 0 or RedemarrageRequis
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, $"pnputil could not restart the device (exit {code}).");
    }

    public Result RemoveDriverPackage(string publie)
    {
        // Le paquet publié est relu dans le magasin : seul un pilote d'affichage se retire par ce geste.
        var inf = Path.Combine(_magasin, publie);
        if (!File.Exists(inf))
            return Result.Fail(ErrorCodes.HelperOpFailed, $"{publie} is not in the driver store; nothing removed.");
        if (!EstUnPiloteDAffichage(File.ReadAllLines(inf)))
            return Result.Fail(ErrorCodes.NotAllowlisted, $"{publie} is not a display driver package; nothing removed.");

        var racine = _creerDurci(_exports);
        if (!racine.IsOk) return racine;
        // Un dossier par retrait : Windows redonne un même oemNN.inf à un autre paquet, et un dossier au seul nom écrasait
        // la sauvegarde du précédent.
        var dossier = Path.Combine(_exports, $"{Path.GetFileNameWithoutExtension(publie)}-{_maintenant():yyyyMMddHHmmssfff}");

        // Sans sauvegarde relue, rien n'est retiré : c'est elle qui rend le geste réversible.
        var (export, _) = _run(["/export-driver", publie, dossier]);
        if (export != 0 || InfsDAffichage(dossier).Count == 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, $"{publie} could not be exported; nothing removed.");

        _run(["/delete-driver", publie]);
        if (File.Exists(inf))
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Windows kept {publie} (a device may still use it).");
        GarderLesDerniersExports();
        return Result.Ok();
    }

    /// <summary>Les exports gardés : un paquet NVIDIA pèse plusieurs Go, et seul Annuler en retirait un. Au-delà des deux
    /// plus récents, les exports partent ; l'Annuler d'un plus ancien dit alors que l'export n'est plus là.</summary>
    internal const int ExportsGardes = 2;

    private void GarderLesDerniersExports()
    {
        try
        {
            // Seuls les dossiers fiables se suppriment en administrateur : un dossier posé d'avance n'est jamais touché.
            var anciens = Directory.GetDirectories(_exports)
                .Where(d => _fiable(d).IsOk)
                .OrderByDescending(d => Path.GetFileName(d)[(Path.GetFileName(d).LastIndexOf('-') + 1)..], StringComparer.Ordinal)
                .Skip(ExportsGardes);
            foreach (var d in anciens)
            {
                try { Directory.Delete(d, recursive: true); }
                catch (Exception) { /* un export qui ne part pas reste, sans gêner le retrait */ }
            }
        }
        catch (Exception) { /* dossier illisible : le retrait reste fait */ }
    }

    public Result RestoreDriverPackage(string publie)
    {
        // Le dernier retrait de ce nom : c'est lui que l'Annuler du geste défait.
        var candidats = Directory.Exists(_exports)
            ? Directory.GetDirectories(_exports, Path.GetFileNameWithoutExtension(publie) + "-*")
                .OrderByDescending(d => d, StringComparer.Ordinal).ToList()
            : [];
        if (candidats.Count == 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, $"No export of {publie} to restore.");
        // Un dossier qu'un programme sans droits pourrait remplir ferait installer son propre pilote en administrateur. Le
        // plus récent est pris parmi les fiables : un dossier posé d'avance au nom plus récent (la réparation de la racine
        // ne descend pas) est sauté, jamais installé, et ne bloque pas la remise du vrai export.
        if (_fiable(_exports) is { IsOk: false } refusRacine)
            return refusRacine;
        Result? refus = null;
        var dossier = candidats.FirstOrDefault(d => (refus = _fiable(d)) is { IsOk: true });
        if (dossier is null)
            return refus!;

        var infs = InfsDAffichage(dossier);
        if (infs.Count != 1)
            return Result.Fail(ErrorCodes.HelperOpFailed, $"The export of {publie} does not hold exactly one display driver package.");

        var (code, _) = _run(["/add-driver", infs[0], "/install"]);
        if (code is not (0 or RedemarrageRequis))
            return Result.Fail(ErrorCodes.HelperOpFailed, $"pnputil could not add the exported {publie} back (exit {code}).");
        // Remis dans le magasin, l'export n'a plus d'usage : il part (des centaines de Mo par paquet). Les exports d'un même
        // nom plus anciens restent : Windows redonne un oemNN.inf à un autre paquet, et ce sont alors leurs seules copies.
        try { Directory.Delete(dossier, recursive: true); }
        catch (Exception) { /* un export qui ne part pas reste, sans gêner la remise */ }
        return Result.Ok();
    }

    private static List<string> InfsDAffichage(string dossier) =>
        Directory.Exists(dossier)
            ? Directory.GetFiles(dossier, "*.inf", SearchOption.TopDirectoryOnly).Where(f => EstUnPiloteDAffichage(File.ReadAllLines(f))).ToList()
            : [];

    // La classe d'installation, dans la section [Version] (Microsoft Learn, « INF Version Section ») : Class=Display, ou
    // le GUID de la classe Display.
    private const string GuidAffichage = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly Regex Cle = new(@"^\s*(Class|ClassGuid)\s*=\s*""?([^"";]*?)""?\s*(;.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool EstUnPiloteDAffichage(IEnumerable<string> lignes)
    {
        var dansVersion = false;
        foreach (var brute in lignes)
        {
            var l = brute.Trim();
            if (l.StartsWith('[')) { dansVersion = l.StartsWith("[Version]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!dansVersion || Cle.Match(l) is not { Success: true } m) continue;
            var valeur = m.Groups[2].Value.Trim();
            if (m.Groups[1].Value.Equals("Class", StringComparison.OrdinalIgnoreCase)
                    ? valeur.Equals("Display", StringComparison.OrdinalIgnoreCase)
                    : valeur.Equals(GuidAffichage, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // CM_Locate_DevNodeW puis CM_Get_DevNode_Status (cfgmgr32, Microsoft Learn) : DN_HAS_PROBLEM dit que Windows tient ce
    // périphérique pour en panne. Introuvable ou illisible : null, rien n'est relancé.
    private const uint DnHasProblem = 0x00000400;

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint probleme, uint devInst, uint flags);

    private static bool? EnPanne(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var noeud, instanceId, 0) != 0) return null;
            if (CM_Get_DevNode_Status(out var etat, out _, noeud, 0) != 0) return null;
            return (etat & DnHasProblem) != 0;
        }
        catch (Exception) { return null; }
    }

    private static (int, string) Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Pnputil) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Une installation de pilote peut prendre du temps ; au-delà, le processus est arrêté et l'échec dit.
            var r = ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(120));
            return r.Fini ? (r.Code, r.Sortie) : (-1, "");
        }
        catch { return (-1, ""); }
    }
}
