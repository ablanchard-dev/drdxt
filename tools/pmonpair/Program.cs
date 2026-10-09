using System.Runtime.InteropServices;
using System.ServiceProcess;

// ============================================================================
// SPIKE — UNE PAIRE CLIENT + SERVICE PRESENTMON ASSORTIE COHABITE-T-ELLE ?
//
// La mesure du 07/09 a montre qu'un client 2.4.1 ne parle PAS a un service 2.5.1. Elle n'autorise
// qu'une conclusion : **la paire de versions utilisee par DrDXT ne cohabite pas avec le service
// Intel installe sur cette machine.** Elle ne dit rien d'une paire ASSORTIE, et c'est la question
// qui decide si le geste utilisateur (« arrete le service rival ») peut disparaitre.
//
// Aucun telechargement n'est necessaire : la machine porte deja la paire 2.5.1 complete —
// `PresentMonAPI2.dll` ET `PresentMonService.exe` cote a cote sous
// `C:\Program Files\Intel\PresentMonSharedService\`, service DEMARRE en automatique.
//
// Cet outil charge une DLL cliente DESIGNEE PAR ARGUMENT (au lieu de celle du dossier de sortie),
// ce qui permet de comparer plusieurs versions dans une seule execution — c'est toute sa raison
// d'etre, et c'est ce qu'aucun test unitaire ne peut faire.
//
//   dotnet run --project tools/pmonpair -- [pid a suivre]
// ============================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── MODE ENFANT : une seule DLL, un seul processus ──────────────────────────────────────────
if (args.Length >= 2 && args[0] == "--enfant")
{
    var chemin = args[1];
    var cible = args.Length > 2 && int.TryParse(args[2], out var q) ? q : 0;

    // Le dossier de la DLL doit etre cherchable : PresentMonAPI2 charge ses propres voisines.
    SetDllDirectory(Path.GetDirectoryName(chemin)!);
    NativeLibrary.SetDllImportResolver(typeof(Api).Assembly,
        (nom, _, _) => nom == "PresentMonAPI2.dll" ? NativeLibrary.Load(chemin) : IntPtr.Zero);

    nint session = 0;
    try
    {
        var rc = Api.pmOpenSession(out session);
        Console.Write($"pmOpenSession = {Nom(rc)}");

        if (rc != 0)
        {
            // Le tube par defaut a echoue : la variante nommee peut-elle faire mieux ?
            var rc2 = Api.pmOpenSessionWithPipe(out session, @"\\.\pipe\sharedpresentmonsvcnamedpipe");
            Console.Write($" | pmOpenSessionWithPipe(defaut) = {Nom(rc2)}");
            rc = rc2;
        }

        if (rc == 0 && cible > 0)
        {
            // L'acquisition commence ici : le service accepte-t-il de suivre un vrai processus ?
            var t = Api.pmStartTrackingProcess(session, (uint)cible);
            Console.Write($" | StartTracking(pid {cible}) = {Nom(t)}");
            if (t == 0) { Thread.Sleep(1500); Api.pmStopTrackingProcess(session, (uint)cible); }
        }

        if (rc == 0) Api.pmCloseSession(session);
        Console.WriteLine();
        return rc == 0 ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{ex.GetType().Name} : {ex.Message}");
        return 2;
    }
}

var pid = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 0;

Console.WriteLine("=== ETAT DES SERVICES ===");
foreach (var nom in new[] { "PresentMonSharedService", "PresentMonService" })
    Console.WriteLine($"  {nom,-26} {Etat(nom)}");

var intelActif = Etat("PresentMonSharedService") == "Running";
Console.WriteLine();
Console.WriteLine(intelActif
    ? "  Le service Intel tourne : c'est la condition de la question posee."
    : "  Le service Intel est ARRETE — rien de ce qui suit ne prouve une cohabitation.");

var versions = new (string Nom, string Dll)[]
{
    ("client 2.5.1 (Intel)", @"C:\Program Files\Intel\PresentMonSharedService\PresentMonAPI2.dll"),
    ("client 2.4.1 (vendore DrDXT)", Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "third_party", "presentmon", "PresentMonAPI2.dll"))),
};

foreach (var (nom, dll) in versions)
{
    Console.WriteLine();
    Console.WriteLine($"=== {nom} ===");
    Console.WriteLine($"  dll : {dll}");
    if (!File.Exists(dll)) { Console.WriteLine("  ABSENTE — essai saute."); continue; }

    // Chaque version tourne dans son PROPRE processus : une fois une PresentMonAPI2.dll chargee,
    // le processus la garde, et la seconde version ne serait jamais lue. Comparer deux DLL de meme
    // nom dans un seul processus donnerait deux fois le meme resultat sans que rien ne le signale.
    var code = Relancer(dll, pid);
    Console.WriteLine($"  → resultat : {code}");
}

Console.WriteLine();
Console.WriteLine("=== CE QUE CE SPIKE PERMET DE CONCLURE ===");
Console.WriteLine("  Un SUCCES du client 2.5.1 contre le service Intel 2.5.1 ACTIF signifie que DrDXT");
Console.WriteLine("  peut consommer le service deja present, donc ne plus en installer un second —");
Console.WriteLine("  et le geste « arrete le service rival » disparait.");

return 0;

// -- l'enfant : charge UNE dll et tente une session ---------------------------------------------
static string Relancer(string dll, int pid)
{
    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    psi.ArgumentList.Add("--enfant");
    psi.ArgumentList.Add(dll);
    psi.ArgumentList.Add(pid.ToString());

    using var proc = System.Diagnostics.Process.Start(psi)!;
    var sortie = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
    proc.WaitForExit(30000);
    return sortie.Trim().Replace("\n", "\n            ");
}

static string Etat(string nom)
{
    try { using var sc = new ServiceController(nom); return sc.Status.ToString(); }
    catch { return "non installe"; }
}

/// <summary>Le nom lisible d'un PM_STATUS. 12 = PIPE_ERROR, celui qui a tout declenche.</summary>
static string Nom(int rc) => rc switch
{
    0 => "SUCCES",
    1 => "FAILURE",
    2 => "SESSION_NOT_OPEN",
    5 => "SERVICE_ERROR",
    7 => "INVALID_PID",
    12 => "PIPE_ERROR(12)",
    _ => $"PM_STATUS={rc}",
};

[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool SetDllDirectory(string path);

/// <summary>Les quatre points d'entree que ce spike exerce. Le nom du type sert d'ancre a
/// <c>SetDllImportResolver</c> : c'est lui qui decide QUELLE PresentMonAPI2.dll est chargee.</summary>
internal static class Api
{
    [DllImport("PresentMonAPI2.dll")]
    internal static extern int pmOpenSession(out nint handle);

    [DllImport("PresentMonAPI2.dll", CharSet = CharSet.Ansi)]
    internal static extern int pmOpenSessionWithPipe(out nint handle, string controlPipe);

    [DllImport("PresentMonAPI2.dll")]
    internal static extern int pmCloseSession(nint handle);

    [DllImport("PresentMonAPI2.dll")]
    internal static extern int pmStartTrackingProcess(nint handle, uint pid);

    [DllImport("PresentMonAPI2.dll")]
    internal static extern int pmStopTrackingProcess(nint handle, uint pid);
}
