using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// helperhandshake — LIVE proof of the helper launch + session-key handshake.
//
// End to end, with NO pre-shared key:
//   1. locate DrDxt.ElevatedHelper.exe next to this tool.
//   2. launch it ELEVATED in HANDSHAKE mode (HelperLauncher + RunAsProcessLauncher
//      -> interactive UAC). The helper is told to trust THIS exe as the caller
//      (--app-path=<this exe>); the SESSION KEY is NOT passed — it is negotiated.
//   3. connect + run the REAL handshake (HandshakingElevatedClient): authenticate
//      the caller, negotiate the ephemeral per-session key.
//   4. execute a harmless allowlisted op (Ping) through the negotiated, signed,
//      allowlisted typed IPC.
//
// Each step prints PASS/FAIL; exit 0 = all PASS. This is the human-run boundary
// proof (the real `runas` spawn + UAC is the untested unit edge). See README.md.
// -----------------------------------------------------------------------

Console.WriteLine("=== DrDXT helperhandshake — launch + §9.13 handshake live proof ===\n");
PrintHowToRun();

var allPass = true;

// --- Step 0: locate the helper exe next to this tool ---
var baseDir = AppContext.BaseDirectory;
var helperExe = Path.Combine(baseDir, "DrDxt.ElevatedHelper.exe");
var thisExe = Environment.ProcessPath;

if (string.IsNullOrWhiteSpace(thisExe))
{
    Fail("Could not resolve this process path (needed as the trusted caller image).");
    return 3;
}
if (!File.Exists(helperExe))
{
    Fail($"Helper exe not found next to this tool: {helperExe}");
    Console.WriteLine("   Build this tool so the ProjectReference copies the helper, e.g.:");
    Console.WriteLine("     dotnet build tools/helperhandshake/helperhandshake.csproj -c Debug");
    return 3;
}
Pass($"Step 0 located helper: {helperExe}");

// --- Step 1: launch the helper ELEVATED in handshake mode (interactive UAC) ---
var pipeName = PipeSecurityFactory.DefaultPipeName;
var spec = new HelperLaunchSpec(helperExe, thisExe!, pipeName);
var launcher = new HelperLauncher(new RunAsProcessLauncher(), new PipeHandshakeAttempt(pipeName));

Console.WriteLine("\n  Launching the elevated helper — APPROVE the UAC prompt...");
var ensure = await launcher.EnsureRunningAsync(spec, default);
if (!ensure.IsOk)
{
    if (ensure.Error!.Value.Code == HelperLauncher.ElevationDeclined)
        Fail("Step 1 launch: UAC was declined (ELEVATION_DECLINED). Re-run and approve the prompt.");
    else
        Fail($"Step 1 launch: {ensure.Error.Value.Code} — {ensure.Error.Value.Message}");
    return 1;
}
Pass("Step 1 helper launched + serving (UAC approved, pipe is up)");

// --- Step 2 + 3: handshake + harmless Ping through the negotiated typed IPC ---
var client = new HandshakingElevatedClient(pipeName, helperExe);
var ping = await client.ExecuteAsync(new PingOp(), default);
if (ping.IsOk)
    Pass("Step 2 handshake + Ping round-tripped OK (ephemeral key negotiated, op allowlisted + signed)");
else
{
    Fail($"Step 2 handshake/Ping: {ping.Error!.Value.Code} — {ping.Error.Value.Message}");
    allPass = false;
}

// --- Step 3: a second op proves the channel keeps working (fresh handshake/op) ---
var ping2 = await client.ExecuteAsync(new PingOp(), default);
if (ping2.IsOk)
    Pass("Step 3 second Ping round-tripped OK (per-connection handshake repeats cleanly)");
else
{
    Fail($"Step 3 second Ping: {ping2.Error!.Value.Code} — {ping2.Error.Value.Message}");
    allPass = false;
}

// --- Step 4: LE CANAL DE RETOUR DE DONNEES, sur le chemin de PRODUCTION ---
// Avant, cette lecture n'apprenait que « la variable est lisible » : les octets etaient
// jetes cote helper. Ici on exige la VALEUR. SecureBoot est choisie parce qu'elle est dans
// l'ensemble ferme de l'allowlist, sous le GUID EFI global, et qu'elle tient en un octet.
var read = await client.ReadAsync(new ReadFirmwareVariableOp("SecureBoot", OpAllowlist.EfiGlobalVariableGuid), default);
if (read.IsOk && read.Value is { Length: > 0 })
{
    var hex = Convert.ToHexString(read.Value);
    // 1 = Secure Boot actif, 0 = inactif. On imprime les DEUX : l'octet brut et sa lecture.
    var meaning = read.Value[0] switch { 1 => "Secure Boot ON", 0 => "Secure Boot OFF", _ => "valeur inattendue" };
    Pass($"Step 4 lecture firmware SecureBoot = 0x{hex} ({meaning}) — le canal rend des OCTETS, plus seulement « lisible »");
}
else if (read.IsOk)
{
    // Un Ok vide serait exactement le defaut corrige : le dire, pas le compter PASS.
    Fail("Step 4 lecture firmware: OK mais AUCUN octet — le canal de retour ne porte rien.");
    allPass = false;
}
else
{
    // 1314 = privilege inactif = NON CONCLUANT, pas un refus du firmware. Le motif est imprime
    // verbatim justement pour qu'on ne confonde pas les deux.
    Fail($"Step 4 lecture firmware: {read.Error!.Value.Code} — {read.Error.Value.Message}");
    allPass = false;
}

// --- Step 5: LIRE LE VARSTORE CONSTRUCTEUR ---------------------------------------
// Ce n'est pas un PASS/FAIL du produit, c'est une MESURE. Le GUID du varstore AMI est un
// CANDIDAT : ce depot ne peut pas le citer depuis une specification, donc c'est la carte qui
// tranche. « Setup » rend 203 sous le GUID EFI global (releve le 27/08) : il vit ailleurs, et cette
// etape demande ou.
//
// Les trois reponses possibles sont distinctes, et les confondre serait le defaut :
//   des octets -> le GUID est le bon SUR CETTE CARTE (fait mesure, pas suppose) ;
//   203        -> la variable n'est pas sous CE GUID non plus -> le candidat est FAUX ici ;
//   1314       -> privilege inactif = NON CONCLUANT, ni l'un ni l'autre.
var setup = await client.ReadAsync(
    new ReadFirmwareVariableOp("Setup", OpAllowlist.AmiSetupVarstoreGuid), default);
Console.WriteLine();
Console.WriteLine("  [MESURE] L3.1 — varstore constructeur, lecture seule");
Console.WriteLine($"           GUID candidat : {OpAllowlist.AmiSetupVarstoreGuid}");
if (setup.IsOk && setup.Value is { Length: > 0 } bytes)
{
    Console.WriteLine($"           -> {bytes.Length} octets LUS. Le candidat est le bon sur cette carte.");
    Console.WriteLine($"           -> 32 premiers octets : {Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)))}");
}
else
{
    Console.WriteLine($"           -> {setup.Error?.Code} : {setup.Error?.Message}");
    Console.WriteLine("           -> a lire tel quel : 203 = pas sous ce GUID ; 1314 = non concluant ;");
    Console.WriteLine("              « larger than the read buffer » = trouve, mais plus gros que 16 Ko.");
}

Console.WriteLine();
Console.WriteLine(allPass ? "=== OVERALL: PASS ===" : "=== OVERALL: FAIL ===");
Console.WriteLine("(The elevated helper keeps running; close its window or end the process to stop it.)");
return allPass ? 0 : 1;

// --- helpers ---
static void Pass(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  [PASS] " + m); Console.ForegroundColor = c; }
static void Fail(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  [FAIL] " + m); Console.ForegroundColor = c; }

static void PrintHowToRun()
{
    Console.WriteLine("How to run this LIVE proof (PowerShell):");
    Console.WriteLine("  1) Build it (copies the helper next to it):");
    Console.WriteLine("       dotnet build tools/helperhandshake/helperhandshake.csproj -c Debug");
    Console.WriteLine("  2) Run it (it will raise ONE UAC prompt to launch the elevated helper):");
    Console.WriteLine(@"       tools\helperhandshake\bin\Debug\net8.0-windows\helperhandshake.exe");
    Console.WriteLine("  NOTE: no session key is pre-shared — the key is negotiated over the pipe");
    Console.WriteLine("        AFTER the helper authenticates this exe as the caller.");
    Console.WriteLine();
}
