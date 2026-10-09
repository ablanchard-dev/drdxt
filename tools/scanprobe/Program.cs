using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Scoring;
using DrDxt.Application.Support;
using DrDxt.Detection.Settings;
using DrDxt.Application.DependencyInjection;
using DrDxt.Profiles;
using DrDxt.App;
using Microsoft.Extensions.DependencyInjection;

// ─────────────────────────────────────────────────────────────────────────────────────────
// scanprobe — ce que le PRODUIT dit de CETTE machine, sans ouvrir de fenetre.
//
// Il monte le conteneur reel (`AddDrDxtDetection`), fait la vraie detection materielle, la
// vraie lecture d'etat, et imprime les constats que `ScoringRules` produit. Rien n'est
// simule : c'est la meme chaine que l'application, moins l'interface.
//
// Pourquoi il existe. « Les tests passent » et « ca marche sur la machine » sont deux
// affirmations differentes, et seule la seconde interesse celui qui utilise le logiciel.
// Cette boucle a deja paye la lecon : un conseiller entierement teste, branche nulle part,
// se comporte exactement comme un conseiller qui marche -- jusqu'a ce qu'on regarde.
//
// LECTURE PURE, NON ELEVE. Il ne repare rien, n'ecrit rien, ne demande aucun UAC.
//
// Usage :  dotnet run -c Release --project tools/scanprobe
// ─────────────────────────────────────────────────────────────────────────────────────────

var sp = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();

// [diagnostic] le lecteur d'ecrans repond-il, et que rend-il ?
var ecr = sp.GetService<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>();
Console.WriteLine($"[diag] IDisplayInfo resolu : {(ecr is null ? "NON" : ecr.GetType().Name)}");
if (ecr is not null)
{
    var tous = ecr.GetAllDisplays();
    Console.WriteLine($"[diag] GetAllDisplays    : IsOk={tous.IsOk} erreur={tous.Error?.Code} n={(tous.Value?.Count.ToString() ?? "-")}");
    foreach (var d in tous.Value ?? Array.Empty<DrDxt.Application.Tweaks.Hardware.DisplayInfo>())
        Console.WriteLine($"[diag]   ecran {d.DeviceName} {d.RefreshHz} Hz (max {d.MaxRefreshHz}) principal={d.IsPrimaryGameDisplay}");
}

var hw = await sp.GetRequiredService<IHardwareDetector>().DetectAsync();
if (!hw.IsOk)
{
    Console.WriteLine($"detection materielle KO : {hw.Error?.Message}");
    return 1;
}

var st = await sp.GetRequiredService<ISettingsStateReader>().ReadAsync(hw.Value!);

// LM 07/09 — CE QUE LE LECTEUR A REELLEMENT LU, avant tout jugement. Sans ces trois lignes, un
// constat absent se lit comme « rien a signaler » alors qu'il veut dire « je n'ai rien lu ».
if (st.IsOk && st.Value is { } vu)
{
    Console.WriteLine($"[lecture] ecrans      : {(vu.AttachedRefreshHz is null ? "JAMAIS LU" : string.Join(" / ", vu.AttachedRefreshHz) + " Hz")}");
    Console.WriteLine($"[lecture] coeurs parques : {(vu.ParkedCoreIndexes is null ? "JAMAIS LU" : vu.ParkedCoreIndexes.Count.ToString())}");
    Console.WriteLine($"[lecture] cartes reseau  : {(vu.NetworkAdapters is null ? "JAMAIS LU" : vu.NetworkAdapters.Count.ToString())}");

    // 18/09/2026 — LES REGLAGES QUI DECIDENT DU PLAN. Sur la machine de reference, les DIX
    // lignes du plan sortaient « ne s'applique pas », et rien ne disait pourquoi : impossible de
    // distinguer « deja regle » de « je n'ai pas su lire ». Ce sont exactement ces TriState qui
    // ferment les leviers de latence, et `Unknown` n'y veut jamais dire « rien a faire ».
    Console.WriteLine($"[lecture] Game Mode      : {vu.GameMode}");
    Console.WriteLine($"[lecture] HAGS (effectif): {vu.HagsEffective}   (cle brute : {vu.Hags})");
    Console.WriteLine($"[lecture] NVIDIA perf max: {vu.NvidiaPreferMaxPerf}");
    Console.WriteLine($"[lecture] VRR            : {vu.Vrr}   (ecran capable : {vu.VrrCapableDisplay})");
    Console.WriteLine($"[lecture] fichier d'echange : {vu.PagefileConfigured}");
    Console.WriteLine($"[lecture] cap d'images   : {(vu.FrameCap is { } cap ? $"{cap.Verdict} — {cap.Reason}" : "JAMAIS LU")}");
}
if (!st.IsOk)
{
    Console.WriteLine($"lecture d'etat KO : {st.Error?.Message}");
    return 1;
}

var state = st.Value!;
var inv = hw.Value!;

// 24/09/2026 — le BIOS LU, par la meme fonction que l'application (helper deja lance, sinon
// lecture directe si ce processus est administrateur ; jamais d'UAC).
var biosLu = await new DrDxt.Bios.NvramBiosSettingsReader(DrDxt.App.Platform.VariablesFirmware.Lire)
    .ReadAsync(inv.Board.Product, inv.Bios.Version);
Console.WriteLine("=== REGLAGES LUS DANS LE BIOS ===");
Console.WriteLine($"carte / BIOS       : {inv.Board.Product} / {inv.Bios.Version}");
if (biosLu.Count == 0) Console.WriteLine("rien de lu (hors table, ou pas les droits)");
foreach (var l in biosLu) Console.WriteLine($"{l.Setting,-18} : {l.Libelle}  (brut {l.Raw}, {(l.IsDefault ? "usine" : "modifie")})");
Console.WriteLine($"XMP deduit         : {state.XmpExpo}");
state = DrDxt.Bios.NvramBiosSettingsReader.Appliquer(state, biosLu);
Console.WriteLine($"XMP apres lecture  : {state.XmpExpo}   ReBAR : {state.ReBar}   CSM : {state.Csm}   (lecture gardee : {state.BiosLu is not null})");
Console.WriteLine();

Console.WriteLine("=== LA MACHINE, TELLE QUE LE PRODUIT LA LIT ===");
Console.WriteLine($"chassis            : {inv.Chassis}");
Console.WriteLine($"XMP/EXPO           : {state.XmpExpo}   (frequence configuree : {state.MemoryConfiguredMhz} MT/s)");
Console.WriteLine($"modules memoire    : {inv.Memory.Count}");
foreach (var m in inv.Memory)
    Console.WriteLine($"  {m.DeviceLocator ?? "(emplacement non lu)",-24} " +
                      $"{m.ConfiguredMhz}/{m.RatedMhz} MT/s  rangs={m.Ranks}  {m.ConfiguredVoltageMv} mV  {m.PartNumber}");

// null et liste vide ne disent PAS la meme chose, et tout ce lot repose sur la difference.
Console.WriteLine(state.UncleanShutdowns is { } shut
    ? $"arrets non propres : {shut.Count} lus sur les 90 derniers jours"
    : "arrets non propres : JAMAIS LU (aucun seam, ou journal illisible)");

// CE QUE LE PRODUIT SAIT LIRE ET NE REGARDE PAS PENDANT UN SCAN.
// `NvmlHardwareProbe.Lire()` rend les watts du GPU, et `HardwareReading` n'est consomme que par
// `BenchmarkRunner` : pendant un scan, personne ne lit la consommation au repos. Or c'est le SEUL
// effet qui joue 24 h sur 24, et c'est exactement le chiffre qui manque a
// `Finding.Display.MixedRefreshIdlePower` -- le seul constat qui parle du repos, et qui arrive
// sans aucun nombre parce que les 30-40 W qu'il pourrait citer viennent d'AUTRES machines.
// Mesure du 18/09/2026 sur la machine de reference : 55,3 W, min = max sur 25 echantillons.
// Ce n'est pas une estimation d'ailleurs, c'est un fait d'ici -- et l'interdit de chiffrer que
// porte l'`IdlePowerAdvisor` vise les gains promis, pas une lecture locale.
// Ou ce chiffre doit apparaitre sur l'ecran du client est une decision produit : le panneau
// ci-dessus n'existe que dans ce diagnostic, l'application n'a pas d'equivalent.
var sonde = new DrDxt.Detection.NvmlHardwareProbe();
var lecture = sonde.Lire();
Console.WriteLine(lecture.GpuPowerW is { } w
    ? $"GPU au repos       : {w:0.0} W" + (lecture.GpuTemperatureC is { } deg ? $"   {deg:0} C" : "")
    : "GPU au repos       : JAMAIS LU (NVML indisponible)");

Console.WriteLine();
Console.WriteLine("=== CE QUE LE PRODUIT DIT ===");
var findings = ScoringRules.Evaluate(inv, state);

// Le compte d'abord, et jamais << aucun >> : cette boucle a appris qu'un rapport qui
// annonce zero probleme sans dire sur quoi il a regarde ne se verifie pas.
var bySeverity = findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key);
Console.WriteLine($"{findings.Count} constats sur {inv.Memory.Count} modules et {inv.Gpus.Count} GPU lus");
foreach (var g in bySeverity)
    Console.WriteLine($"  {g.Key,-8} : {g.Count()}");

Console.WriteLine();
foreach (var f in findings.OrderByDescending(f => f.Severity))
    Console.WriteLine($"[{f.Severity,-6}] {f.Category,-9} {f.TitleKey}" +
                      (f.EstimatedGainPct is { } g2 ? $"   gain annonce {g2}" : "   (aucun chiffre)"));

Console.WriteLine();
Console.WriteLine("Une cle brute ci-dessus est une cle de ressource : ce que le client lit est sa");
Console.WriteLine("traduction. Un constat SANS cle dans les deux resx atteindrait l'ecran en brut.");

// ── LE RAPPORT SAV, celui que le client colle dans son ticket ────────────────────────────
// Deux lignes de GATES le decrivent depuis le 05/09 : le « Motif » sortirait en ANGLAIS dans
// un rapport francais, et des identifiants bruts atteindraient le client. Toutes deux sont
// HERITEES : personne ne les a relancees depuis. `DiagnosticReport.Build` est pur, donc il se
// rend ici sans ouvrir de fenetre ni de boite de dialogue.
if (args.Contains("--rapport"))
{
    var scorer = sp.GetRequiredService<IScanScorer>();
    var scan = scorer.Score(inv, state, baseline: null);
    Console.WriteLine();
    Console.WriteLine("=== RAPPORT SAV, mot pour mot ===");
    // Lot 1 : la preuve du besoin porte AUSSI la sante de la machine, lue par le meme audit que
    // l'ecran (lecture seule : `Lire()` n'ecrit rien, c'est l'outil `repairaudit` qui journalise).
    var sante = RepairTriage.Trier(new DrDxt.Detection.Repair.RepairAuditService().Lire());
    Console.WriteLine(DiagnosticReport.Build(
        scan.IsOk ? scan.Value : null, appVersion: "1.0.0",
        when: DateTimeOffset.UtcNow, sante: sante));
}

// ── LE PLAN, ce que l'ecran appelle « optimisations recommandees » ───────────────────────
// La vue gratuite n'en montre que la categorie (« Latence : optimisation recommandee »). Cette
// section imprime CE QUI est recommande, pour pouvoir juger chaque ligne sur la machine.
if (args.Contains("--plan"))
{
    // 16/09/2026 : la premiere version montait AddDrDxtApplication seul, donc les lecteurs de TEST
    // (registre, plan d'alimentation, services) qui echouent toujours : « non lu ⇒ applicable » faisait
    // sortir comme recommande ce qui etait deja regle. On monte ici le conteneur de l'APPLI elle-meme
    // et on appelle le meme consommateur de profil que l'ecran.
    var appli = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
    var inventaire = await appli.GetRequiredService<IHardwareDetector>().DetectAsync();
    var etat = await appli.GetRequiredService<ISettingsStateReader>().ReadAsync(inventaire.Value!);
    var plan = appli.GetRequiredService<DrDxt.App.Profiles.IScanProfileConsumer>().Consume(inventaire.Value!, etat.Value!);
    Console.WriteLine();
    // 18/09/2026 — LES MEMES LECTURES, MAIS PAR LES LECTEURS DE L'APPLI. Le bloc `[lecture]` du
    // haut monte `AddDrDxtDetection` seul : les seams NVIDIA et ecran y sont des bouchons, donc
    // le cap d'images y sort « JAMAIS LU » pour une raison qui tient a la SONDE, pas au produit.
    // Comparer les deux colonnes est le seul moyen de ne pas prendre un trou d'outillage pour un
    // trou de produit — erreur commise ici meme le 18/09, et corrigee en mesurant les deux.
    if (etat.IsOk && etat.Value is { } vuApp)
    {
        Console.WriteLine("=== LES MEMES REGLAGES, LUS PAR L'APPLI ===");
        Console.WriteLine($"[appli] Game Mode      : {vuApp.GameMode}");
        Console.WriteLine($"[appli] HAGS (effectif): {vuApp.HagsEffective}   (cle brute : {vuApp.Hags})");
        Console.WriteLine($"[appli] NVIDIA perf max: {vuApp.NvidiaPreferMaxPerf}");
        Console.WriteLine($"[appli] VRR            : {vuApp.Vrr}   (ecran capable : {vuApp.VrrCapableDisplay})");
        Console.WriteLine($"[appli] cap d'images   : {(vuApp.FrameCap is { } capApp ? $"{capApp.Verdict} — {capApp.Reason}" : "JAMAIS LU")}");
        Console.WriteLine();
    }
    Console.WriteLine("=== LE PLAN RECOMMANDE, avec les lecteurs reels de l'appli ===");
    foreach (var item in plan.Items)
        Console.WriteLine($"{item.TweakId,-40} s'applique={item.Applicable,-5} au_plan={item.InExecutedPlan,-5} teste={item.Tested,-5} {item.AdjustmentNote}");
    Console.WriteLine($"affiche a l'ecran (au plan) : {string.Join(", ", plan.Items.Where(i => i.InExecutedPlan).Select(i => i.TweakId))}");
}

return 0;
