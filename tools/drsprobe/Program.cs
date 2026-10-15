using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Display;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using Microsoft.Extensions.DependencyInjection;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.GpuNative.NvApi;
using Microsoft.Win32;

// Sonde. Le cap 141 devait etre OBSERVE dans le driver, pas deduit de la formule
// (refresh - 3). Lecture seule PAR DEFAUT ; --set-frl <n> est le seul chemin qui ecrit,
// et il existe parce que la suite de tests du depot ecrasait ce reglage sur la machine
// de dev (NvApiDrsActionsTests ecrivait 141 en dur, sans restaurer) : il faut pouvoir
// rendre a la machine la valeur qu-elle avait.
{
    var si = Array.IndexOf(args, "--set-frl");
    if (si >= 0 && args.Length > si + 1 && uint.TryParse(args[si + 1], out var wanted))
    {
        var w = new NvApiNative();
        var avant = w.TryReadDrsGlobalSetting(0x10835002u, out var b) ? b.Value.ToString() : "?";
        var ok = w.TrySetDrsGlobalSetting(0x10835002u, wanted);
        // On ne croit PAS l-ecriture sur parole : on relit.
        var apres = w.TryReadDrsGlobalSetting(0x10835002u, out var a) ? a.Value.ToString() : "?";
        Console.WriteLine($"FRL_FPS {avant} -> demande {wanted} -> relu {apres}  (ecriture {(ok ? "OK" : "REFUSEE")})");
        Console.WriteLine(apres == wanted.ToString() ? "  confirme par relecture" : "  LA RELECTURE NE CONFIRME PAS");
        return;
    }
}

// --roundtrip <chemin complet> [--j-autorise-une-ecriture]
// La SEULE preuve MACHINE du revert par suppression : lit, ecrit LA MEME valeur au profil de cet
// executable, relit, supprime, relit, et imprime les trois provenances. Sans le second drapeau il
// LIT et s'arrete. A lancer sur un profil sans enjeu (un exe dont NVIDIA livre le profil), jamais
// pendant une partie. Session elevee requise pour l'ecriture.
{
    var ri = Array.IndexOf(args, "--roundtrip");
    if (ri >= 0 && args.Length > ri + 1)
    {
        const uint Pstate = 0x1057EB71u; // PREFERRED_PSTATE_ID
        var chemin = args[ri + 1];
        var n = new NvApiNative();
        string Lire() => n.TryReadDrsSettingForApplication(chemin, Pstate, out var r)
            ? $"{r.Value} ({DrsSettingOrigin.Describe(r.SettingLocation, r.IsCurrentPredefined, r.IsPredefinedValid).Origin})"
            : "non lu (pas de profil propre ?)";

        Console.WriteLine($"  {chemin}");
        Console.WriteLine($"  1. avant           : PREFERRED_PSTATE = {Lire()}");
        if (!args.Contains("--j-autorise-une-ecriture"))
        {
            Console.WriteLine("  AUCUNE ECRITURE. Ajouter --j-autorise-une-ecriture pour l'aller-retour.");
            return;
        }
        if (!n.TryReadDrsSettingForApplication(chemin, Pstate, out var initial))
        {
            Console.WriteLine("  Pas de profil propre : rien a ecrire, rien a supprimer.");
            return;
        }

        var ecrit = n.TrySetDrsSettingForApplication(chemin, Pstate, initial.Value);
        Console.WriteLine($"  2. meme valeur ({(ecrit ? "OK" : "REFUSE")}) : {Lire()}   attendu : SetByUser");
        var supprime = n.TryDeleteDrsSettingForApplication(chemin, Pstate);
        Console.WriteLine($"  3. supprime ({(supprime ? "OK" : "REFUSE")})    : {Lire()}   attendu : la provenance de l'etape 1");
        return;
    }
}

// --app <chemin complet> : le profil que le driver applique VRAIMENT a cet executable.
// Un controle positif ET un controle negatif sont necessaires : une struct mal alignee
// echouerait proprement, et on conclurait "pas de profil" a tort.
{
    var ai = Array.IndexOf(args, "--app");
    if (ai >= 0 && args.Length > ai + 1)
    {
        var reader = new NvApiLatencyReader(new NvApiNative(), new Reg());
        // Les quatre reglages que le catalogue sait ecrire, lus au scope du JEU.
        (uint Id, string Nom)[] reglages =
            [(0x1057EB71, "PREFERRED_PSTATE"), (0x10835002, "FRL_FPS"),
             (0x00A879CF, "VSYNCMODE"), (0x007BA09E, "PRERENDERLIMIT")];
        foreach (var chemin in args[(ai + 1)..])
        {
            Console.WriteLine($"  {chemin}");
            foreach (var (id, nom) in reglages)
            {
                var r = reader.GetDrsForApplication(id, chemin);
                Console.WriteLine(r.IsOk
                    ? $"      {nom} = {r.Value.Value}  provenance = {r.Value.Origin.Origin}"
                    : $"      {nom} : non lu au scope du jeu ({r.Error?.Message})");
            }
        }
        return;
    }
}

// --state : la chaine COMPLETE, telle que le produit la monte. Les tests prouvent que le
// verdict existe ; ceci prouve que le PRODUIT le prononce sur une vraie machine. La lecon
// de la nuit : "les tests passent" et "ca marche sur la machine" sont deux affirmations
// differentes, et seule la seconde interesse celui qui utilise le logiciel.
if (args.Contains("--state"))
{
    var sp = new ServiceCollection()
        .AddDrDxtDetection()
        .AddSingleton<INvApi>(new NvApiLatencyReader(new NvApiNative(), new Reg()))
        .AddSingleton<IDisplayInfo>(new Win32DisplayInfo())
        .BuildServiceProvider();

    var hw = await sp.GetRequiredService<IHardwareDetector>().DetectAsync();
    if (!hw.IsOk) { Console.WriteLine($"detection materielle KO : {hw.Error?.Message}"); return; }

    var st = await sp.GetRequiredService<ISettingsStateReader>().ReadAsync(hw.Value!);
    if (!st.IsOk) { Console.WriteLine($"lecture d-etat KO : {st.Error?.Message}"); return; }

    Console.WriteLine("=== ce que le PRODUIT dit de cette machine ===");
    Console.WriteLine(st.Value!.FrameCap is { } fc
        ? $"  cap d-images : {fc.Verdict}\n      {fc.Reason}"
        : "  cap d-images : non calcule (seams absents)");
    Console.WriteLine(st.Value!.ParkedCoreIndexes is { } pk
        ? $"  coeurs parques : {pk.Count} -> [{string.Join(", ", pk)}]"
        : "  coeurs parques : NON LU (et ce n-est pas la meme chose que zero)");
    Console.WriteLine(st.Value.Restart is { } rs
        ? $"  redemarrage  : {rs.Verdict}\n      {rs.Reason}"
        : "  redemarrage  : non calcule");
    // La cle et l'etat effectif, cote a cote : c'est leur ecart que le produit lisait mal.
    Console.WriteLine($"  HAGS         : cle HwSchMode = {st.Value.Hags}, effectif (D3DKMT) = {st.Value.HagsEffective}");
    return;
}

// Lecture seule a partir d-ici.
// formula (refresh - 3). Nothing here writes.
var nv = new NvApiLatencyReader(new NvApiNative(), new Reg());
Console.WriteLine($"NVAPI available : {nv.IsAvailable}");
var cap = nv.GetFrameRateLimit();
var vs = nv.GetVsyncMode();
Console.WriteLine(cap.IsOk ? $"  FRL_FPS   (0x10835002) = {cap.Value}"
                           : $"  FRL_FPS   FAIL {cap.Error?.Code}: {cap.Error?.Message}");
var origine = nv.GetFrameRateLimitWithOrigin();
if (origine.IsOk)
    Console.WriteLine($"  provenance             = {origine.Value.Origin.Origin} " +
                      $"(location {(origine.Value.Origin.IsYourOwnChange ? "profil courant, pose par un humain" : "voir motif")})");
Console.WriteLine(vs.IsOk ? $"  VSYNCMODE (0x00A879CF) = 0x{vs.Value:X8}"
                          : $"  VSYNCMODE FAIL {vs.Error?.Code}: {vs.Error?.Message}");

// PREFERRED_PSTATE — l'etat COURANT du seul candidat dont l'application soit prouvable
// aujourd'hui. 0 = Adaptive (defaut pilote), 1 = Prefer maximum performance. Lecture seule.
var ps = nv.GetPreferredPstate();
Console.WriteLine(ps.IsOk
    ? $"  PREFERRED_PSTATE (0x1057EB71) = {ps.Value} "
      + (ps.Value == 1 ? "(Prefer max performance)" : ps.Value == 0 ? "(Adaptive — defaut pilote)" : "(?)")
    : $"  PREFERRED_PSTATE FAIL {ps.Error?.Code}: {ps.Error?.Message}");

var all = new Win32DisplayInfo().GetAllDisplays();
foreach (var d in all.Value ?? [])
    Console.WriteLine($"  {d.DeviceName} primary={d.IsPrimaryGameDisplay} {d.RefreshHz} Hz (max {d.MaxRefreshHz}) vrr={d.SupportsVrr}");

// Horodatage du boot et de la base DRS : la CORRELATION des deux dit si le profil a ete
// reecrit APRES le demarrage (donc par quelque chose qui tourne) ou s'il arrive deja faux.
var verdict = FrameCapInferencer.Infer(cap.IsOk ? (int)cap.Value : null, all.Value);
Console.WriteLine();
Console.WriteLine($"  VERDICT : {verdict.Verdict}");
Console.WriteLine($"  {verdict.Reason}");
Console.WriteLine();
Console.WriteLine($"  boot            : {DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64):dd/MM HH:mm:ss}");
var db = new FileInfo(@"C:\ProgramData\NVIDIA Corporation\Drs\nvdrsdb0.bin");
if (db.Exists)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var fs = db.OpenRead();
    Console.WriteLine($"  nvdrsdb0.bin    : ecrit {db.LastWriteTime:dd/MM HH:mm:ss}  sha {Convert.ToHexString(sha.ComputeHash(fs))[..16]}");
}
Console.WriteLine($"  lu le           : {DateTime.Now:dd/MM HH:mm:ss}");

// ---------------------------------------------------------------------------------
// --watch : le guet. Le cap revient "tout seul" et personne n'a jamais VU l'instant ou
// il change. Lire une valeur au prochain demarrage ne donne que le RESULTAT ; ce mode
// enregistre le MOMENT, avec ce qui vient de demarrer autour. Lecture seule : il
// n'ecrit que son propre journal.
// ---------------------------------------------------------------------------------
if (args.Contains("--watch"))
{
    // Le dossier est surchargeable pour que le GUET LUI-MEME soit prouvable : un guet qui ne
    // se declenche jamais est pire que pas de guet, et on ne peut pas forcer NVIDIA a ecrire
    // sur commande. --watch <dossier> verifie la plomberie sur un dossier temoin.
    var wi = Array.IndexOf(args, "--watch");
    var drsDir = wi >= 0 && args.Length > wi + 1 ? args[wi + 1] : @"C:\ProgramData\NVIDIA Corporation\Drs";
    var journal = Path.Combine(AppContext.BaseDirectory, "guet-cap-nvidia.log");
    Log(journal, $"=== guet demarre {DateTime.Now:dd/MM HH:mm:ss} - cap initial {(cap.IsOk ? cap.Value.ToString() : "illisible")} [{verdict.Verdict}] ===");
    Console.WriteLine($"Guet arme sur {drsDir}");
    Console.WriteLine($"Journal : {journal}");

    uint? last = cap.IsOk ? cap.Value : null;
    void Relire(string quoi)
    {
        Thread.Sleep(400); // laisser le driver finir d'ecrire avant de relire
        var now = new NvApiLatencyReader(new NvApiNative(), new Reg()).GetFrameRateLimit();
        var v = now.IsOk ? (uint?)now.Value : null;
        // Tout evenement est trace (preuve que le guet vit) ; un CHANGEMENT de valeur sort
        // en entier avec son contexte.
        if (v == last)
        {
            Log(journal, $"{DateTime.Now:dd/MM HH:mm:ss}  {quoi}  cap inchange ({v?.ToString() ?? "?"})");
            return;
        }
        var d = new Win32DisplayInfo().GetAllDisplays();
        var verd = FrameCapInferencer.Infer(v is null ? null : (int)v.Value, d.Value);
        var ecrans = string.Join(" | ", (d.Value ?? []).Select(x => $"{(x.IsPrimaryGameDisplay ? "P" : "-")}{x.RefreshHz}Hz"));
        var jeunes = string.Join(", ", System.Diagnostics.Process.GetProcesses()
            .Select(pr => { try { return (Nom: pr.ProcessName, Debut: pr.StartTime); } catch { return (Nom: null!, Debut: DateTime.MinValue); } })
            .Where(x => x.Nom is not null && x.Debut > DateTime.Now.AddMinutes(-3))
            .OrderByDescending(x => x.Debut).Take(12).Select(x => $"{x.Nom}@{x.Debut:HH:mm:ss}"));
        Log(journal, $"{DateTime.Now:dd/MM HH:mm:ss}  {quoi}  cap {last?.ToString() ?? "?"} -> {v?.ToString() ?? "?"}  [{verd.Verdict}]  ecrans {ecrans}");
        Log(journal, $"    demarres < 3 min : {(string.IsNullOrEmpty(jeunes) ? "aucun" : jeunes)}");
        Log(journal, $"    {verd.Reason}");
        last = v;
    }

    using var watcher = new FileSystemWatcher(drsDir)
    {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        IncludeSubdirectories = false,
        EnableRaisingEvents = true,
    };
    watcher.Changed += (_, e) => Relire($"ecrit {e.Name}");
    watcher.Created += (_, e) => Relire($"cree {e.Name}");
    watcher.Renamed += (_, e) => Relire($"renomme {e.Name}");
    Thread.Sleep(Timeout.Infinite);
}

static void Log(string path, string line)
{
    Console.WriteLine(line);
    File.AppendAllText(path, line + Environment.NewLine);
}


sealed class Reg : IRegistryReader
{
    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive == RegistryHiveKind.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var k = root.OpenSubKey(path, false);
            return Result<uint?>.Ok(k?.GetValue(name) is int i ? (uint)i : null);
        }
        catch (Exception ex) { return Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, ex.Message); }
    }

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive == RegistryHiveKind.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var k = root.OpenSubKey(path, false);
            return Result<string?>.Ok(k?.GetValue(name) is string[] a ? string.Join(';', a) : null);
        }
        catch (Exception ex) { return Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, ex.Message); }
    }

    public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive == RegistryHiveKind.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var k = root.OpenSubKey(path, false);
            return Result<string?>.Ok(k?.GetValue(name) as string);
        }
        catch (Exception ex) { return Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, ex.Message); }
    }
}
