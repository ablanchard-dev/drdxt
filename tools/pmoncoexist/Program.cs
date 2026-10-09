using System.ServiceProcess;
using DrDxt.Core;
using DrDxt.Measurement;
using DrDxt.Measurement.PresentMon;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================
// SMOKE DE COEXISTENCE PRESENTMON.
//
// La question, et elle est binaire : **le service Intel etant actif, DrDXT ouvre-t-il quand meme
// sa session de mesure ?** Le produit demandait jusqu'ici a l'utilisateur d'arreter le service
// rival -- un geste manuel, a refaire a chaque demarrage, puisque `PresentMonSharedService`
// demarre en AUTOMATIQUE.
//
// CET OUTIL A REPONDU NON, ET IL A DIT POURQUOI (07/09/2026). Le serveur ignore
// `--control-pipe` : aucun tube prive n'est possible, ni en 2.4.1 ni avec le binaire Intel 2.5.1.
// Et le refus n'est meme pas un conflit de nom -- le client 2.4.1 atteint le service 2.5.1 et se
// fait refuser (PM_STATUS 12), la ou il reussit avec un service 2.4.1. C'est une incompatibilite
// de VERSION. Tout est ecrit dans `docs/PRESENTMON-COEXISTENCE.md`.
//
// L'outil reste : c'est lui qui verifie la non-regression du chemin de mesure, et c'est lui qui
// dira si une future API cliente 2.5.x peut consommer le service Intel deja present.
//
// Il exerce le canal de CONTROLE (pmOpenSessionWithPipe), pas la capture de frames -- mais
// c'est exactement la que le conflit se produisait.
//
//   dotnet run --project tools/pmoncoexist            (constate)
//   dotnet run --project tools/pmoncoexist -- --reparer  (installe/repare par le code du produit)
// ============================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--reparer"))
{
    // Le VRAI chemin du produit : le meme code que le helper elevé execute pour
    // `InstallPresentMonServiceOp`. Install si absent, REPAIR si le binPath n'est pas le notre,
    // rien a ecrire s'il l'est deja — puis demarrage. Exige une console administrateur.
    Console.WriteLine("=== REPARATION PAR LE CODE DU PRODUIT (Win32ServiceActions) ===");
    var binaire = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "third_party", "presentmon", "PresentMonService.exe"));
    Console.WriteLine($"  binaire vendore : {binaire} ({(File.Exists(binaire) ? "present" : "ABSENT")})");

    var actions = new DrDxt.ElevatedHelper.Execution.Win32ServiceActions(
        presentMonBinaryLocator: () => binaire);
    var r = actions.InstallPresentMonService();
    Console.WriteLine($"  resultat        : IsOk={r.IsOk} {(r.IsOk ? "" : r.Error!.Value.Code + " — " + r.Error.Value.Message)}");
    Console.WriteLine();
}

Console.WriteLine("=== LES DEUX SERVICES, TELS QUE LE SCM LES VOIT ===");
foreach (var nom in new[] { "PresentMonService", "PresentMonSharedService" })
    Console.WriteLine($"  {nom,-26} {EtatService(nom)}");

Console.WriteLine();
Console.WriteLine("=== LES TUBES NOMMES PUBLIES ===");
// Un tube nomme se lit comme un fichier de \\.\pipe\ : c'est la seule facon de savoir QUI a
// gagne la course aux noms par defaut.
var tubes = Directory.GetFiles(@"\\.\pipe\")
    .Select(Path.GetFileName)
    .Where(n => n is not null &&
        (n.Contains("presentmon", StringComparison.OrdinalIgnoreCase) ||
         n.Contains("drdxt", StringComparison.OrdinalIgnoreCase)))
    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (tubes.Count == 0)
    Console.WriteLine("  (aucun) — aucun service de mesure n'a publie de tube");
foreach (var t in tubes)
    Console.WriteLine($"  {t}");

var tubeAttendu = Path.GetFileName(PresentMonNaming.ControlPipe);
var tubePublie = tubes.Any(t => t!.StartsWith(tubeAttendu, StringComparison.OrdinalIgnoreCase));

Console.WriteLine();
Console.WriteLine($"  tube attendu par le client : {tubeAttendu}");
Console.WriteLine($"  publie ?                   : {(tubePublie ? "OUI" : "NON")}");
Console.WriteLine("  il n'existe qu'UN nom possible : le serveur ignore --control-pipe");
Console.WriteLine("     (mesure du 07/09/2026, docs/PRESENTMON-COEXISTENCE.md)");

Console.WriteLine();
Console.WriteLine("=== QUEL TUBE REPOND, ET AVEC QUEL CODE ===");
// L'outil appelle la DLL en direct : c'est le seul moyen de comparer PLUSIEURS noms de tube
// dans une meme execution, alors que le produit n'en connait qu'un. C'est sa raison d'etre.
foreach (var (etiquette, tube) in new (string, string?)[]
         {
             ("tube du produit  ", PresentMonNaming.ControlPipe),
             ("tube par defaut  ", @"\\.\pipe\sharedpresentmonsvcnamedpipe"),
             ("sans nom de tube ", null),
         })
{
    nint h = 0;
    string resultat;
    try
    {
        var rc = tube is null ? PmOpenSession(out h) : PmOpenSessionWithPipe(out h, tube);
        resultat = rc == 0 ? "SUCCES" : $"PM_STATUS={rc}";
        if (rc == 0) PmCloseSession(h);
    }
    catch (Exception ex) { resultat = ex.GetType().Name; }

    Console.WriteLine($"  {etiquette} {tube ?? "(pmOpenSession)",-45} -> {resultat}");
}

Console.WriteLine();
Console.WriteLine("=== CE QUE LE CLIENT DrDXT OBTIENT ===");

// Le vrai chemin du produit : PresentMonSession -> PresentMonApi2 -> pmOpenSessionWithPipe.
var sp = new ServiceCollection().AddDrDxtMeasurement().BuildServiceProvider();
var session = sp.GetRequiredService<IPresentMonSession>();

var dispo = session.IsServiceAvailable();
Console.WriteLine($"  IsServiceAvailable : IsOk={dispo.IsOk} valeur={(dispo.IsOk ? dispo.Value.ToString() : "-")}");
Console.WriteLine($"  detail             : {session.LastUnavailableDetail ?? "(aucun — la session s'est ouverte)"}");

var intelActif = EtatService("PresentMonSharedService") == "Running";

Console.WriteLine();
Console.WriteLine("=== VERDICT ===");
if (dispo.IsOk && dispo.Value)
{
    Console.WriteLine("Le client DrDXT ouvre sa session de mesure.");
    if (intelActif)
        Console.WriteLine("  Et le service Intel tourne EN MEME TEMPS — la coexistence serait prouvee.");
}
else
{
    Console.WriteLine("ECHEC : le client n'ouvre pas de session.");
    Console.WriteLine(tubePublie
        ? "  Le tube EST publie et la session echoue quand meme : c'est une incompatibilite de\n" +
          "  VERSION, pas un conflit de nom. Le client 2.4.1 ne parle pas a un service 2.5.1."
        : "  Aucun service PresentMon n'a publie le tube : demarre celui de DrDXT.");
    if (intelActif)
        Console.WriteLine("  Le service Intel tient le nom : un seul service peut le posseder.");
}

// Le succes n'a de valeur que si le rival tournait aussi : c'est la question posee.
return dispo.IsOk && dispo.Value && intelActif ? 0 : 1;

[System.Runtime.InteropServices.DllImport("PresentMonAPI2.dll", EntryPoint = "pmOpenSessionWithPipe",
    CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
static extern int PmOpenSessionWithPipe(out nint handle, string controlPipe);

[System.Runtime.InteropServices.DllImport("PresentMonAPI2.dll", EntryPoint = "pmOpenSession")]
static extern int PmOpenSession(out nint handle);

[System.Runtime.InteropServices.DllImport("PresentMonAPI2.dll", EntryPoint = "pmCloseSession")]
static extern int PmCloseSession(nint handle);

static string EtatService(string nom)
{
    try
    {
        using var sc = new ServiceController(nom);
        return $"{sc.Status}";
    }
    catch
    {
        return "non installe";
    }
}
