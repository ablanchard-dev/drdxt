using System.Diagnostics;
using DrDxt.Application.Autopilot;
using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.Detection.Scoring;
using DrDxt.Measurement;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================
// « OPTIMISER CE JEU » — LA VERTICALE, EXERCEE POUR DE VRAI.
//
// C'est ce que le bouton fera : mesurer la partie, savoir ce que cette partie permet de
// voir, tenter un reglage pertinent, le relire, mesurer les deux etats en alternance,
// et garder UNIQUEMENT ce qui ameliore le jeu au-dessus du bruit de la session.
//
// Toute la chaine est reelle : PresentMon mesure, les leviers du PRODUIT ecrivent (ceux que le
// selecteur retient sur cette machine), chaque ecriture est relue.
//
// Un resultat parfaitement valide est « aucun reglage conserve : cette partie etait
// trop bruitee ». C'est infiniment preferable a un faux +3 %.
//
//   dotnet run --project tools/optimize -- [pid] [secondesParPasse]
//   (sans pid : le premier processus qui presente des frames)
// ============================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 30/09/2026 (boucle « la carte, prouvée », G3) : `--repos-carte`, jeu fermé. Rend « Normal » au profil global
// du pilote, mesure la consommation de la carte avant et après, et retire le réglage sans baisse nette.
if (args.Contains("--repos-carte"))
    return await ReposDeLaCarte(args.Contains("--suivi"));

var pidArg = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 0;
var secondes = args.Length > 1 && double.TryParse(args[1], out var s)
    ? s
    : GameOptimizationSession.DureeParPasse.TotalSeconds;

Console.WriteLine("== DrDXT — OPTIMISER CE JEU ==");

// ── 1. QUEL JEU ? ───────────────────────────────────────────────────────────────────────
var cible = pidArg > 0 ? Process.GetProcessById(pidArg) : Deviner();
if (cible is null)
{
    Console.WriteLine("   Aucun jeu en cours. Lance une partie, puis relance — DrDXT mesure ce que");
    Console.WriteLine("   tu joues, il ne simule rien.");
    return 3;
}
Console.WriteLine($"   jeu       : {cible.ProcessName} (pid {cible.Id})");
Console.WriteLine($"   objectif  : Competitive — {GameObjective.Explication(OptimizationGoal.Competitive)}");
Console.WriteLine($"   passe     : {secondes:0.#} s");
Console.WriteLine();

// ── 2. LA CHAINE DE MESURE REELLE DU PRODUIT ────────────────────────────────────────────
// Le meme `IBenchmarkRunner` que le scan utilise : il sait deja reparer son propre service
// de mesure quand il ne repond pas (SelfHealingBenchmarkRunner).
var sp = new ServiceCollection()
    .AddSingleton<IElevatedClient>(new ElevationDirecte())
    // Le focus OBSERVE : sans cette ligne il resterait UNKNOWN, et une passe jouee ailleurs
    // compterait comme une performance du PC.
    .AddSingleton<IForegroundProbe, DrDxt.Detection.Win32ForegroundProbe>()
    // La telemetrie materielle, echantillonnee PENDANT chaque passe : sans elle le root-cause
    // parlerait d'un autre moment du jeu que celui ou la decision a ete prise.
    .AddSingleton<IHardwareProbe>(new DrDxt.Detection.NvmlHardwareProbe())
    .AddDrDxtMeasurement()
    .BuildServiceProvider();
var banc = sp.GetRequiredService<IBenchmarkRunner>();

// 24/09/2026 — `--diag` : UNE passe, et ce que le banc rend tel quel. Une session qui rend « aucune
// mesure exploitable » ne dit pas si c'est PresentMon, le focus ou zero image : ceci le dit.
if (args.Contains("--diag"))
{
    var d = await banc.RunAsync(cible.Id, TimeSpan.FromSeconds(secondes), 1, CancellationToken.None);
    Console.WriteLine(d.IsOk && d.Value is { } v
        ? $"   diag : {v.AvgFps:0.0} img/s moy, 1 % low {v.P1LowFps:0.0}, focus {v.ForegroundFraction?.ToString("0.00") ?? "non observe"}"
        : $"   diag : ECHEC {d.Error?.Code} — {d.Error?.Message}");
    if (d.IsOk && d.Value is { FrameTimesMs: { Count: > 0 } ft })
    {
        var t = ft.OrderBy(x => x).ToArray();
        double P(double q) => t[Math.Min(t.Length - 1, (int)(t.Length * q))];
        var med = P(0.5);
        Console.WriteLine($"   frametimes : n={t.Length} min={t[0]:0.00} med={med:0.00} p99={P(0.99):0.00} p99.9={P(0.999):0.00} max={t[^1]:0.00} ms"
                          + $" ; >2x mediane : {t.Count(x => x > 2 * med)} ; <0.5x mediane : {t.Count(x => x < 0.5 * med)}"
                          + $" ; instrument {d.Value.Instrument ?? "PresentMon"}");
    }
    return 0;
}

// ── 2 bis. LE LOT : le catalogue entier, juge comme l'App le juge ─────────────────────────
// LU AVANT TOUTE ECRITURE, et en rejeu aussi : ce ne sont que des lectures (journal WER,
// journal System). Le lot sort de `JugementApply`, la MEME fonction que l'ecran de l'App : un
// levier que le selecteur ecarte — veto des arrets brutaux compris — n'y entre jamais. Le goulot
// n'est pas encore mesure ici : `UnknownPartial`, qui ne ferme rien et ne promet rien.
var jugement = JugementApply.Juger(
    DiagnosticDeScan.Construire(goulot: null, mesure: null,
        StabiliteLue.InstabiliteGpu(new WerLiveKernelReportLog()),
        StabiliteLue.ArretsBrutaux(new WindowsUnexpectedShutdownLog(), DateTimeOffset.UtcNow)),
    CatalogueDeCandidats.Ids.ToList(), jeuEnCours: true);
AfficherLeLot(jugement);

// -- MODE REJEU : la meme boucle, sur de VRAIES frames de jeu deja capturees -------------
// Ce n'est pas un mode degrade, c'est un CONTROLE. Les captures de `tools/p3` viennent de
// vraies parties de Warzone (01/09/2026) : elles portent la derive, le bruit et les morts
// d'une session reelle. Exercer la boucle dessus prouve la chaine de DECISION sur des
// donnees authentiques, sans demander a personne de rejouer neuf minutes.
if (args.Contains("--rejeu"))
{
    var captures = Directory.GetFiles(Path.Combine(RacineDepot(), "tools", "p3"), "frames-*.txt")
        .OrderBy(f => f).ToList();
    var rejeu = new BancDeRejeu(captures);
    Console.WriteLine($"   REJEU : {rejeu.Source} (Warzone, 01/09/2026) decoupee en "
                      + $"{rejeu.Tranches} tranches d'une minute");
    Console.WriteLine("   Les DEUX candidats ne changent RIEN : ce sont des CONTROLES, et le lot juge plus");
    Console.WriteLine("   haut n'est PAS tente (aucune ecriture en rejeu). Un verdict autre que « non");
    Console.WriteLine("   resolu » signalerait que la boucle voit un effet la ou il n'y en a pas.");
    // La fabrique du produit est CONSTRUITE pour de vrai (lectures seulement, aucun Apply) : ce
    // qui ne se construirait pas en campagne se voit ici, sans rien ecrire.
    var fabriqueLue = FabriqueDuProduit(new NvApiTuningActions(), offsetDeReference: 0);
    // Le lot du SELECTEUR, meme quand le veto de campagne le vide : c'est la fabrique qu'on exerce.
    foreach (var d in jugement.LotAChaud)
        Console.WriteLine($"   fabrique  : {d.Id,-30}"
                          + (fabriqueLue(d) is null ? "cette version ne sait PAS le construire" : "construit (non applique)"));
    // LA TAILLE D'ECHANTILLON EST LE NOMBRE DE TRANCHES, PAS LE NOMBRE DE PASSES. Le banc
    // recycle ses tranches en boucle : le protocole demande plus de passes qu'il n'existe de
    // tranches, donc chacune revient plusieurs fois. Un plan de ce depot a compte les passes et
    // annonce « 24 passes » la ou il n'y avait que 9 minutes distinctes — trois fois trop, et un
    // seuil de significativite fabrique avec.
    Console.WriteLine($"   n = {rejeu.Tranches} tranches DISTINCTES, recyclees en boucle : c'est"
                      + " CE nombre qui borne");
    Console.WriteLine("   ce qu'on a le droit de conclure, jamais le nombre de passes affiche plus bas.");
    Console.WriteLine();

    var profilRejeu = new GameOptimizationSession(rejeu,
            () => new GameProfileIdentity("Warzone (rejeu)", "NVIDIA RTX 4060", PiloteNvidia(),
                Environment.OSVersion.VersionString, ModeAffichage()))
        .Run(1, new IAutopilotCandidate[]
            {
                new CandidatNeutre("controle A (n'ecrit rien)"),
                new CandidatNeutre("controle B (n'ecrit rien)"),
            }, OptimizationGoal.Competitive, passesAa: 3, dureeParPasse: TimeSpan.FromSeconds(60));

    Afficher(profilRejeu, TimeSpan.Zero);
    return 0;
}

// Le veto se lit AVANT la moindre lecture de carte : un lot vide ne touche a rien.
if (!args.Contains("--controle") && jugement.LotAChaud.Count == 0)
{
    Console.WriteLine("   AUCUN candidat tente : " + (jugement.VetoDesArrets is { } veto
        ? "veto des arrets — " + veto + "."
        : "le selecteur les a tous ecartes (motifs ci-dessus)."));
    Console.WriteLine("   Rien n'a ete ecrit. C'est son diagnostic, pas un echec de la campagne.");
    return 0;
}

var tuning = new NvApiTuningActions();
var offsetInitial = tuning.GetCoreClockOffsetMhz(0);
if (!offsetInitial.IsOk)
{
    Console.WriteLine($"   offset GPU illisible ({offsetInitial.Error?.Code}) : sans relecture,");
    Console.WriteLine("      aucun reglage ne peut etre applique honnetement.");
    return 2;
}

// LE LOT DE CANDIDATS — L'HISTORIQUE D'UNE DECISION, PUIS LA REGLE EN VIGUEUR.
//
// 07/09/2026 — UN SEUL CANDIDAT. L'offset coeur +100 MHz etait le seul AutoHot reellement ecrit et
// reversible a chaud, et il etait pourtant ECARTE : la machine porte 13 arrets non propres
// inexpliques (GATES.md), et la decision du 07/09 est « on reste a 0 MHz / 100 % » — on n'ajoute
// pas une variable non necessaire a une machine qu'on debogue. Mieux vaut zero optimisation
// qu'une optimisation risquee. `--controle` exercait donc toute la chaine avec un candidat qui
// n'ecrit RIEN : le seul moyen honnete de verifier que la boucle ne fabrique pas de gain, puisqu'un
// candidat inerte DOIT ressortir « non resolu ». Ce mode reste, inchange.
//
// 11/09/2026 — LE LOT EST ELARGI AU CATALOGUE (decision produit). La regle :
//   1. le lot est `jugement.LotAChaud`, calcule plus haut par la MEME fonction que l'App. Le
//      veto des arrets brutaux (L6), la decision « 0 MHz / 100 % » (`TuningGpuAutorise: false`
//      dans `DiagnosticDeScan`) et l'instabilite graphique repetee y sont appliques AVANT : ils
//      priment sur l'elargissement. Le veto des arrets ferme sur la SURFACE ecrite (materiel ET
//      pilote graphique) : trouve en rejeu le 11/09, trois leviers du pilote passaient l'ancien veto
//      L6, fonde sur la famille declaree. Il a d'abord ete ferme ICI par un second veto propre a la
//      campagne ; corrige a la source le meme soir, ce second veto a ete retire ;
//   2. chaque candidat est mesure SEUL, en A-B-B-A, puis garde ou annule AVANT que le suivant ne
//      soit pose (`AutopilotEngine`, une boucle par candidat) : jamais deux leviers non juges dans
//      la machine en meme temps. Un candidat garde reste pose pendant les suivants — c'est l'etat
//      de depart qu'ils doivent battre ;
//   3. le verdict final ne compose que les candidats GARDES (`GainCumule.DepuisLaCampagne`).
// Pourquoi c'est sur : l'elargissement change le NOMBRE de candidats, pas un seul garde-fou. Un
// seul levier est en essai a la fois, chaque essai relit ce qu'il ecrit et revient sur echec, et
// chaque levier du produit passe par son journal de revert (`pending-revert.json`), que l'App rend
// au demarrage suivant si l'outil meurt pendant l'essai. Teste : `JugementApplyLotTests` (veto
// actif ⇒ zero candidat materiel, la raison dite) et `CampagneAPlusieursCandidatsTests` (ordre,
// total des seuls gardes).
IReadOnlyList<IAutopilotCandidate> candidats;
if (args.Contains("--controle"))
{
    candidats = new IAutopilotCandidate[] { new CandidatNeutre() };
    Console.WriteLine("   candidat  : CONTROLE (n'ecrit rien) — le lot n'est pas tente");
}
else
{
    var fabrique = FabriqueDuProduit(tuning, offsetInitial.Value);
    var lot = new List<IAutopilotCandidate>();
    foreach (var d in jugement.LotAChaud)
    {
        if (fabrique(d) is { } c) lot.Add(c);
        else Console.WriteLine($"   {d.Id} : retenu, mais cette version ne sait pas le construire — non tente");
    }
    if (lot.Count == 0)
    {
        Console.WriteLine("   AUCUN candidat tente : aucun levier retenu n'est constructible ici. Rien n'a ete ecrit.");
        return 0;
    }
    candidats = lot;
    Console.WriteLine($"   lot       : {lot.Count} candidat(s), un a la fois — "
                      + string.Join(", ", lot.Select(c => c.Descriptor.Id)));
}

// ── 3. LA SESSION ───────────────────────────────────────────────────────────────────────
// DrDXT ne sait PAS reconnaitre un lobby, et pretendre le contraire serait pire que de
// demander. La representativite est donc DECLAREE ; sans declaration elle reste Unknown, et un
// keep durable est refuse. Le silence ne vaut jamais « gameplay ».
var descriptionScene = args.FirstOrDefault(a => a.StartsWith("--scene="))?["--scene=".Length..];
var contratScene = new SceneContract(
    descriptionScene is null ? SceneRepresentativity.Unknown : SceneRepresentativity.Gameplay,
    args.Contains("--validation") ? SceneRole.Validation : SceneRole.Decouverte,
    descriptionScene);

var session = new GameOptimizationSession(banc, () => Identite(cible.ProcessName, tuning));
var duree = TimeSpan.FromSeconds(secondes);

Console.WriteLine($"   mesure en cours — {secondes:0.#} s par passe, au pire "
                  + $"{3 + GameOptimizationSession.AlternancesMax} passes. JOUE : une passe pendant "
                  + "laquelle le jeu n'est pas au premier plan est ecartee et refaite.");
var chrono = Stopwatch.StartNew();
var profil = SessionAvecDuree(session, cible.Id, candidats, duree, contratScene);
chrono.Stop();

Afficher(profil, chrono.Elapsed);

// 11/09/2026 — cet outil n'a PAS de manifeste signe : un levier qu'il garde n'est rendu par aucune
// desinstallation. Son origine reste dans pending-revert.json (Gardes), que rien ne rejoue. On le dit.
foreach (var c in candidats.OfType<TweakCandidate>())
    if (c.OrigineHorsManifeste is { } raison)
        Console.WriteLine($"   ATTENTION {c.Descriptor.Id} garde : origine HORS du manifeste signe ({raison}). "
                          + "Desinstaller DrDXT ne le remettra pas ; l'origine reste dans pending-revert.json (Gardes). "
                          + "/ kept: its original value is NOT in the signed manifest; uninstalling DrDXT will not restore it.");

// ── 4bis. LE CONTROLE ALIMENTE LA CALIBRATION ───────────────────────────────────────────
// Il ACCUMULE, il ne corrige rien. La tentation serait de regarder trois no-op et d'ajuster
// les intervalles ; ce serait exactement la faute qu'on vient de fermer ailleurs — trancher sur
// un echantillon trop petit. Sous vingt observations, le juge refuse de conclure, et ce depot ne
// fait que remplir le compteur.
if (args.Contains("--controle"))
{
    var resultatControle = profil.Results.FirstOrDefault();
    var observations = (resultatControle?.Metriques ?? Array.Empty<MetricEvidence>())
        .Where(m => double.IsFinite(m.Preuve.DeltaPct) && double.IsFinite(m.Preuve.ResolutionPct))
        .Select(m => new NoOpObservation(
            DateTimeOffset.UtcNow,
            contratScene.Description ?? contratScene.Representativite.ToString(),
            secondes,
            resultatControle!.PassesAb,
            // LE MINIMUM DES PASSES RETENUES, PAS DE TOUTES LES OBSERVATIONS.
            //
            // Trouve en session Warzone live le 08/09/2026 : le journal de focus contient AUSSI
            // les passes ecartees et refaites (ici 64 %, 0 %, 41 %). En prendre le minimum brut
            // donnait 0 %, et le juge de calibration ecartait alors une experience parfaitement
            // saine — « 3 observations deposees », puis « 0 controles » pour les trois grandeurs.
            //
            // Une passe ecartee n'a contribue AUCUNE donnee a l'experience. L'exposition reelle
            // est celle des passes retenues, et c'est elle qui doit qualifier l'observation.
            profil.FocusParPasse?.OfType<double>()
                .Where(f => f >= FocusObservation.FractionMinimale)
                .DefaultIfEmpty(1.0).Min(),
            m.Metrique,
            m.Preuve.DeltaPct,
            m.Preuve.ResolutionPct))
        .ToList();

    var magasin = new NoOpCalibrationStore(new FileSystemPerformanceJournalFile(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DrDXT", "noop-calibration.json")));
    var depot = magasin.Ajouter(observations);

    Console.WriteLine();
    Console.WriteLine(depot.IsOk
        ? $"   calibration : {observations.Count} observation(s) de controle deposees"
        : $"   calibration : depot impossible ({depot.Error?.Message})");

    foreach (var m in GameMetricSet.Toutes)
    {
        var r = magasin.Juger(m, secondes, resultatControle?.PassesAb ?? 0);
        Console.WriteLine($"   {"",-13}{GameMetricSet.Libelle(m),-16}{r.Observations,4} controles"
                          + $"   {r.Verdict}"
                          + (r.Verdict == CalibrationVerdict.PasAssezDObservations
                              ? $" (il en faut {NoOpCalibration.ObservationsMinimum})"
                              : $"   depassements {r.Depassements}/{r.Observations}"));
    }
}

// ── 5. LA GARANTIE ──────────────────────────────────────────────────────────────────────
var final = new NvApiTuningActions().GetCoreClockOffsetMhz(0);
// Seul `gpu.core_offset` bouge l'offset ; tant que la decision « 0 MHz » tient, il n'entre pas
// dans le lot et l'offset doit etre relu IDENTIQUE a celui du depart.
var attendu = profil.Kept.Any(r => r.Id == "gpu.core_offset") ? 100 : offsetInitial.Value;
Console.WriteLine();
Console.WriteLine($"   etat final : offset attendu {attendu:+#;-#;0} MHz, relu "
                  + $"{(final.IsOk ? final.Value.ToString("+#;-#;0") : "?")} MHz — "
                  + (final.IsOk && final.Value == attendu ? "conforme." : "NON CONFORME."));

return final.IsOk && final.Value == attendu ? 0 : 1;

static void Afficher(GameOptimizationProfile profil, TimeSpan duree)
{
    Console.WriteLine();
    Console.WriteLine($"== PROFIL OPTIMISE - {profil.Identity.ProcessName} =="
                      + (duree > TimeSpan.Zero ? $"  ({duree.TotalMinutes:0.0} min)" : ""));
    Console.WriteLine($"   {profil.Identity.GpuName} - pilote {profil.Identity.GpuDriver}"
                      + $" - {profil.Identity.DisplayMode}");
    Console.WriteLine();

    Console.WriteLine(profil.Baseline is { } b
        ? $"   baseline  : {b.AvgFps:0.0} FPS moyens - 1 % low {b.P1LowFps:0.0} FPS"
        : "   baseline  : aucune mesure exploitable");
    Console.WriteLine($"   objectif  : {GameObjective.Explication(profil.Goal)}");
    Console.WriteLine($"   la partie : {profil.SceneExplanation}");
    if (profil.ResolutionMoyenne is { } rm && profil.ResolutionUnPourcentLow is { } rl)
    {
        Console.Write($"   la scene  : moyenne {Res(rm)} - 1 % low {Res(rl)}");
        Console.WriteLine(profil.ResolutionP95Frametime is { } rp ? $" - p95 frametime {Res(rp)}" : "");
    }
    if (profil.Scene2 is { } sc)
    {
        Console.WriteLine($"   scene     : {sc.Representativite} / {sc.Role}"
                          + (string.IsNullOrWhiteSpace(sc.Description) ? "" : $" — {sc.Description}"));
        if (sc.CeQuiManque is { } manque)
            Console.WriteLine($"   {"",-10}aucun keep DURABLE ici : {manque}");
    }
    if (profil.MaterielParPasse is { Count: > 0 } mat)
    {
        var watts = mat.Select(x => x.PuissanceMedianeW).OfType<double>().ToList();
        var degres = mat.Select(x => x.TemperatureMaxC).OfType<double>().ToList();
        var mhz = mat.Select(x => x.FrequenceMedianeMhz).OfType<double>().ToList();
        var mhzMin = mat.Select(x => x.FrequenceMinimaleMhz).OfType<double>().ToList();
        Console.WriteLine("   materiel  : "
            + (profil.GpuBusyMedianPct is { } gb ? $"GPU busy {gb:0} % - " : "GPU busy non lu - ")
            + (mhz.Count > 0 ? $"{mhz.Average():0} MHz (min {mhzMin.Min():0}) - " : "MHz non lus - ")
            + (watts.Count > 0 ? $"{watts.Average():0} W - " : "watts non lus - ")
            + (degres.Count > 0 ? $"{degres.Max():0} °C max" : "temperature non lue"));
        Console.WriteLine($"   {"",-10}mesure PENDANT les {mat.Count} passes, pas avant");
        // Une OBSERVATION. Nommer un plafond n'explique pas un ecart de FPS : il faudrait
        // changer la limite et remesurer, ce qui est une experience, pas une lecture.
        Console.WriteLine($"   bridage   : {GpuLimiter.Phrase(profil.Bridage)}");

    // CES GRANDEURS ETAIENT MESUREES A CHAQUE PASSE ET JETEES.
    //
    // `BenchmarkResult` porte `CpuBusyRatio`, `GpuWaitRatio`, `PresentStallMsP95`,
    // `RenderPresentLatencyMsP95` et `AnimationErrorMsP95` depuis des semaines. Le seul
    // consommateur etait `BottleneckClassifier`, qui les ECRASE en un unique etat — les chiffres
    // eux-memes n'atteignaient personne.
    //
    // Et ce sont exactement celles qui repondent a « pourquoi le GPU reste-t-il a 88-90 % ? » :
    // `GpuWaitRatio` dit la part de frame ou le GPU ATTEND, `CpuBusyRatio` le travail CPU de
    // l'application. Une session ETW entiere a ete construite pour une question dont une partie
    // de la reponse etait deja capturee.
    if (profil.MesureDeReference is { } m)
    {
        static string Pct(double? v) => v is { } x ? $"{x:P0}" : "non lu";
        static string Ms(double? v) => v is { } x ? $"{x:0.0} ms" : "non lu";

        Console.WriteLine($"   cadence   : CPU app {Pct(m.CpuBusyRatio)} de la frame - "
                          + $"attente GPU {Pct(m.GpuWaitRatio)} - "
                          + $"blocage Present p95 {Ms(m.PresentStallMsP95)}");
        Console.WriteLine($"   {"",-10}latence render→present p95 {Ms(m.RenderPresentLatencyMsP95)} - "
                          + $"erreur d'animation p95 {Ms(m.AnimationErrorMsP95)}");

        // Meme reserve que dans `tools/smoke`, et pour la meme raison mesuree le 08/09/2026 :
        // sous vingt frames un p95 EST le maximum, et sur une capture lente l'erreur d'animation
        // ne fait que recopier l'intervalle entre deux images. Afficher ces trois nombres sans
        // cette ligne, c'est laisser quelqu'un lire une saccade d'une seconde la ou il n'y a que
        // de l'arithmetique.
        // 27/09/2026 : chaque p95 se juge sur le compte de SA serie, comme l'ecran et `tools/smoke` ; le
        // nombre de trames ne dit rien des series du blocage et de l'erreur d'animation. La latence
        // render-present ne publie pas de compte : elle reste jugee sur les trames exploitables.
        if (TailStatistic.PourquoiPasInterpretable(m.PresentStallSamples ?? 0) is { } pourquoiBlocage)
            Console.WriteLine($"   {"",-10}blocage Present p95 PAS interpretable : {pourquoiBlocage}");
        if (TailStatistic.PourquoiPasInterpretable(m.AnimationErrorSamples ?? 0) is { } pourquoiCadence)
            Console.WriteLine($"   {"",-10}erreur d'animation p95 PAS interpretable : {pourquoiCadence}");
        if (TailStatistic.PourquoiPasInterpretable(FrametimeStatistics.EchantillonsExploitables(m.FrameTimesMs)) is { } pourquoi)
            Console.WriteLine($"   {"",-10}latence render-present p95 PAS interpretable : {pourquoi}");
        // L'erreur d'animation est LA grandeur de cadence : une moyenne de 140 FPS peut etre
        // desagreable a jouer, et c'est la seule mesure qui le dit.
    }

    // LE CLASSIFIEUR DE GOULOT EXISTAIT ET N'AVAIT JAMAIS VU UNE SESSION DE JEU. Il etait
    // branche sur le scan et sur le tableau de bord ; la boucle qui choisit un candidat, elle,
    // decidait sans lui. Il parle enfin de la partie qu'on vient de mesurer.
    var goulot = BottleneckClassifier.Classify(
        profil.MesureDeReference,
        frameCap: null,
        gpuThrottling: profil.Bridage switch
        {
            GpuLimiterObserved.PlafondPuissanceObserve => true,
            GpuLimiterObserved.PlafondThermiqueObserve => true,
            GpuLimiterObserved.Inconnu => null,
            _ => false,
        });

    Console.WriteLine($"   goulot    : {goulot.State} ({goulot.Confidence.ToString().ToLowerInvariant()})"
                      + (goulot.Provisional ? " — PROVISOIRE" : ""));
    Console.WriteLine($"   {"",-10}{goulot.Reason}");
    if (!goulot.AllowsMeasuredGainClaim)
        Console.WriteLine($"   {"",-10}cet etat n'autorise AUCUNE affirmation de gain mesure");
    }
    if (profil.FocusParPasse is { Count: > 0 } f)
        Console.WriteLine("   focus     : " + string.Join(" ", f.Select(x =>
            x is null ? "?" : $"{x.Value:P0}")));
    Console.WriteLine();

    foreach (var r in profil.Results)
    {
        var verdict = r.Outcome switch
        {
            CandidateOutcome.Kept => "CONSERVE",
            CandidateOutcome.RejectedRegression => "rejete (degradation)",
            CandidateOutcome.RejectedUnresolved => "rejete (non resolu)",
            CandidateOutcome.Unstable => "rejete (instable)",
            CandidateOutcome.ApplyFailed => "non applique",
            _ => "non teste",
        };
        Console.WriteLine($"   {r.Id,-24} {verdict}"
                          + (r.PassesAb > 0 ? $"   ({r.PassesAb} passes d'alternance)" : ""));

        // UNE LIGNE PAR GRANDEUR. C'est la sortie que le brief demande : chaque metrique avec
        // son ecart, SA resolution et SON verdict — au lieu d'un seul chiffre qui les melange.
        foreach (var m in r.Metriques ?? Array.Empty<MetricEvidence>())
            // 24/09/2026 — une grandeur INVALIDE dit pourquoi : « NaN % invalide » seul se lisait comme
            // un defaut de calcul, alors que PairedAb refusait d'extraire un effet qu'il n'avait pas.
            Console.WriteLine(m.Preuve.Outcome == EvidenceOutcome.Invalid
                ? $"   {"",-6}{m.Libelle,-16}   aucun chiffre : {m.Preuve.InvalidReason ?? "experience invalide"}"
                : $"   {"",-6}{m.Libelle,-16}{m.Preuve.DeltaPct,8:+0.0;-0.0} %"
                  + $"   resolution +/-{m.Preuve.ResolutionPct,5:0.0} %   {Verdict(m.Preuve.Outcome)}");

        if (!string.IsNullOrWhiteSpace(r.Detail)) Console.WriteLine($"   {"",-6}-> {r.Detail}");
    }

    // ── LE VERDICT DE LA CAMPAGNE, EN UNE PHRASE PAR GRANDEUR ───────────────────────────────
    // Ce qui precede est un TABLEAU. Avec un seul candidat il se lit ; des que la campagne en
    // essaie plusieurs, il faut composer les ecarts de tete — et composer des pourcentages de tete
    // est exactement la faute que `GainCumule` existe pour rendre impossible.
    //
    // UNE PHRASE PAR GRANDEUR, jamais une seule pour les trois. Un composite calcule avant la
    // mesure est la faute deja payee le 07/09 : le score annoncait +/-72 % la ou la moyenne, seule,
    // resolvait a +/-4,6 %. Trois phrases en clair valent mieux qu'un chiffre sans referent.
    //
    // Et le total ne retient que les candidats CONSERVES : un levier rejete a ete annule, son
    // ecart n'est plus dans la machine.
    Console.WriteLine();
    Console.WriteLine("   == CE QUE LA CAMPAGNE A LAISSE DANS LA MACHINE ==");
    foreach (var m in GameMetricSet.Toutes)
        Console.WriteLine($"   {GainCumule.Phrase(GainCumule.DepuisLaCampagne(profil.Results, m))}");
}

static string Verdict(EvidenceOutcome o) => o switch
{
    EvidenceOutcome.ProvenGain => "GAIN PROUVE",
    EvidenceOutcome.ProvenRegression => "REGRESSION PROUVEE",
    EvidenceOutcome.NoResolvedChange => "non resolu",
    _ => "invalide",
};

static string Res(SceneResolution r) => r.Verdict switch
{
    SceneVerdict.Usable => $"+/-{r.ResolutionPct:0.0} %",
    SceneVerdict.DriftDominant => $"rien (elle derive de {r.DriftPctPerRun:+0.0;-0.0} %/passe)",
    _ => "rien (trop peu de passes)",
};

static string RacineDepot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
    return d?.FullName ?? AppContext.BaseDirectory;
}

// ── outils locaux ───────────────────────────────────────────────────────────────────────

static GameOptimizationProfile SessionAvecDuree(
    GameOptimizationSession session, int pid, IReadOnlyList<IAutopilotCandidate> candidats, TimeSpan duree,
    SceneContract scene)
    => session.Run(pid, candidats, OptimizationGoal.Competitive, passesAa: 3,
                   dureeParPasse: duree, scene: scene);

/// <summary>Chaque levier du catalogue, et ce que la machine en fait. « LOT » = sera tente, un a
/// la fois ; « reboot » = retenu mais son effet n'existe qu'apres redemarrage, rien a mesurer ici.</summary>
static void AfficherLeLot(JugementApply j)
{
    Console.WriteLine("== LE LOT : LE CATALOGUE, JUGE SUR CETTE MACHINE ==");
    foreach (var a in j.Appreciations)
    {
        var etiquette = !a.Retenu ? "ecarte "
            : j.LotAChaud.Any(d => d.Id == a.Id) ? "LOT    "
            : j.AuRedemarrage.Contains(a.Id) ? "reboot "
            : "retenu ";
        Console.WriteLine($"   {etiquette}{a.Id,-30}{a.Motifs[0]}");
        foreach (var m in a.Motifs.Skip(1)) Console.WriteLine($"   {"",-37}{m}");
    }
    if (j.VetoDesArrets is { } veto)
        Console.WriteLine($"   VETO DES ARRETS : {veto}. Materiel et pilote graphique sont fermes, "
                          + "pour la campagne comme pour l'App.");
    Console.WriteLine($"   => {j.LotAChaud.Count} candidat(s) a chaud dans le lot de campagne"
                      + (j.AuRedemarrage.Count > 0
                          ? $" ; {j.AuRedemarrage.Count} au redemarrage, jamais tente(s) ici"
                          : ""));
    Console.WriteLine();
}

/// <summary>
/// Un descripteur du lot devient un candidat mesurable, avec les leviers du PRODUIT : la meme
/// fabrique que l'Optimiser de l'App (`TweakCandidate`, journal de revert avant d'ecrire, relecture
/// par `AppliedStateVerifier`). Construire ne lit ni n'ecrit rien sur la machine.
///
/// L'outil tourne deja eleve : les operations privilegiees passent par l'executeur EN PROCESSUS,
/// derriere la meme liste blanche que le helper (`OpAllowlist`). Le reglage GPU y est DESACTIVE,
/// comme dans l'hote de demarrage : la decision « 0 MHz / 100 % » est tenue par construction, en
/// plus du selecteur. Le seul ecrivain GPU de l'outil reste `GpuOffsetCandidate`, que le selecteur
/// ferme tant que la decision tient.
/// </summary>
static Func<CandidateDescriptor, IAutopilotCandidate?> FabriqueDuProduit(
    NvApiTuningActions tuning, int offsetDeReference)
{
    var registre = new DrDxt.App.Platform.Win32RegistryReader();
    var alimentation = new DrDxt.App.Platform.Win32PowerPlanReader();
    var nvApi = new DrDxt.Detection.Gpu.Nvapi.NvApiLatencyReader(
        new DrDxt.GpuNative.NvApi.NvApiNative(), registre);
    var produit = new ServiceCollection()
        .AddDrDxtApplication()
        .AddSingleton<IRegistryReader>(registre)
        .AddSingleton<IPowerPlanReader>(alimentation)
        .AddSingleton<INvApi>(nvApi)
        .AddSingleton<IElevatedClient>(new InProcessElevatedClient(new OpAllowlist(),
            new PrivilegedOpExecutor(new Win32RegistryActions(), new Win32PowerPlanActions(),
                new Win32RestorePointActions(), new Win32ServiceActions(),
                new NvApiDrsActions(), new DisabledNvApiActions(), new DisabledAdlxActions())))
        .BuildServiceProvider();
    var catalogue = produit.GetRequiredService<ITweakCatalog>();
    var filet = FichierRevertEnAttente.ParDefaut();

    return d => d.Id == "gpu.core_offset"
        ? new GpuOffsetCandidate(tuning, gpu: 0, cible: 100, reference: offsetDeReference, d)
        : catalogue.Get(d.Id) is { IsOk: true, Value: { } levier }
            ? new TweakCandidate(levier, d, registre, filet, nvApi, alimentation)
            : null;
}

static async Task<int> ReposDeLaCarte(bool suivi)
{
    var registre = new DrDxt.App.Platform.Win32RegistryReader();
    var nvApi = new DrDxt.Detection.Gpu.Nvapi.NvApiLatencyReader(new DrDxt.GpuNative.NvApi.NvApiNative(), registre);
    var eleve = new InProcessElevatedClient(new OpAllowlist(),
        new PrivilegedOpExecutor(new Win32RegistryActions(), new Win32PowerPlanActions(),
            new Win32RestorePointActions(), new Win32ServiceActions(),
            new NvApiDrsActions(), new DisabledNvApiActions(), new DisabledAdlxActions()));

    Console.WriteLine("== DrDXT : rendre le repos a la carte, et le prouver ==");
    Console.WriteLine($"   global lu avant : {Lu(nvApi.GetPreferredPstate())}");
    if (suivi)
    {
        // Diagnostic : écrit, relit, suit la carte 90 s, puis annule toujours.
        var levier = new DrDxt.Application.Tweaks.Latency.ReposRenduALaCarteTweak(nvApi, eleve);
        var ecrit = await levier.ApplyAsync();
        Console.WriteLine($"   ecrit           : {(ecrit.IsOk ? "oui" : ecrit.Error?.Message)} ; relu : {Lu(nvApi.GetPreferredPstate())}");
        var sonde = new DrDxt.Detection.NvmlHardwareProbe();
        for (var s = 0; s <= 90; s += 5)
        {
            var l = sonde.Lire();
            Console.WriteLine($"   t+{s,2} s : {l.GpuPowerW:0.0} W, {l.GpuClockMhz:0} MHz");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        if (ecrit.IsOk) Console.WriteLine($"   annule          : {((await levier.RevertAsync(ecrit.Value!)).IsOk ? "oui" : "NON")} ; relu : {Lu(nvApi.GetPreferredPstate())}");
        return 0;
    }
    var v = await DrDxt.Application.Tweaks.PreuveDuRepos.ProuverAsync(
        new DrDxt.Application.Tweaks.Latency.ReposRenduALaCarteTweak(nvApi, eleve),
        new DrDxt.Detection.NvmlHardwareProbe(), 20, t => Task.Delay(t));
    Console.WriteLine($"   verdict         : {v.Etat}");
    Console.WriteLine($"   repos avant     : {v.AvantW:0.0} W (mediane de 20 lectures)");
    Console.WriteLine($"   repos apres     : {v.ApresW:0.0} W");
    Console.WriteLine($"   global lu apres : {Lu(nvApi.GetPreferredPstate())}");
    return v.Etat == DrDxt.Application.Tweaks.EtatDeLaPreuve.Prouve ? 0 : 1;

    static string Lu(Result<uint> r) => r.IsOk ? r.Value switch { 0 => "0 (Normal)", 1 => "1 (performances maximales)", var x => x.ToString() } : $"illisible ({r.Error?.Code})";
}

/// <summary>Le premier processus qui presente des frames. Un vrai jeu serait mieux ; a defaut,
/// n'importe quel processus qui presente prouve la meme chaine de mesure.</summary>
static Process? Deviner() => Process.GetProcesses()
    .Where(p => { try { return p.MainWindowHandle != 0 && !p.HasExited; } catch { return false; } })
    .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0L; } })
    .FirstOrDefault();

static GameProfileIdentity Identite(string jeu, NvApiTuningActions tuning)
{
    var gpu = tuning.GetCoreClockOffsetMhz(0).IsOk ? "NVIDIA (NVAPI)" : null;
    return new GameProfileIdentity(
        jeu, gpu, PiloteNvidia(), Environment.OSVersion.VersionString, ModeAffichage());
}

/// <summary>Resolution et rafraichissement de l'ecran principal. Un changement de mode invalide
/// un profil : les FPS d'une session en 1440p@200 ne disent rien d'une session en 1080p@144.</summary>
static string? ModeAffichage()
{
    try
    {
        var mode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, -1, ref mode)
            ? $"{mode.dmPelsWidth}x{mode.dmPelsHeight} @ {mode.dmDisplayFrequency} Hz"
            : null;
    }
    catch { return null; }
}

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

static string? PiloteNvidia()
{
    try
    {
        var n = new DrDxt.GpuNative.Nvml.NvmlNative();
        return n.TryGetDriverVersion(out var v) ? v : null;
    }
    catch { return null; }
}

/// <summary>Le canal eleve : cet outil TOURNE deja eleve, donc il execute directement au lieu de
/// passer par le helper. C'est le seul raccourci de cet outil, et il est explicite.</summary>
internal sealed class ElevationDirecte : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(op is InstallPresentMonServiceOp
            ? new DrDxt.ElevatedHelper.Execution.Win32ServiceActions().InstallPresentMonService()
            : Result.Fail(ErrorCodes.HelperOpFailed, $"{op.GetType().Name} hors perimetre de cet outil."));
}

/// <summary>L'offset coeur GPU. `ReadBack` compare la valeur RELUE a la valeur demandee :
/// mesure du 01/09/2026, une limite de puissance « appliquee a 110 % » se relisait a 100 %.</summary>
internal sealed class GpuOffsetCandidate : IAutopilotCandidate
{
    private readonly NvApiTuningActions _t;
    private readonly int _gpu, _cible, _reference;

    /// <param name="descripteur">Celui du catalogue : le selecteur et le moteur jugent le MEME levier.</param>
    public GpuOffsetCandidate(NvApiTuningActions t, int gpu, int cible, int reference, CandidateDescriptor descripteur)
    {
        _t = t; _gpu = gpu; _cible = cible; _reference = reference;
        Descriptor = descripteur;
    }

    public CandidateDescriptor Descriptor { get; }
    public Result Apply() => _t.SetCoreClockOffset(_gpu, _cible);
    public Result Revert() => _t.SetCoreClockOffset(_gpu, _reference);
    public Result<bool> ReadBack()
    {
        var lu = _t.GetCoreClockOffsetMhz(_gpu);
        return Result<bool>.Ok(lu.IsOk && lu.Value == _cible);
    }
}


/// <summary>Le minimum de DEVMODE pour lire le mode courant : la structure complete n'apporte
/// rien ici, et les champs sont a leur offset natif.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
    CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
internal struct DEVMODE
{
    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields;
    public int dmPositionX, dmPositionY;
    public int dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
    public int dmPanningWidth, dmPanningHeight;
}


/// <summary>Un banc qui rejoue les captures REELLES du 01/09 : une par passe, dans l'ordre. Les
/// frametimes sont ceux d'une vraie partie, avec sa derive, son bruit et ses morts.</summary>
internal sealed class BancDeRejeu : IBenchmarkRunner
{
    private readonly List<List<double>> _tranches;
    private int _passe;

    /// <summary>Decoupe UNE capture en tranches d'une minute. Chaque tranche est une passe
    /// successive de la MEME session : c'est ce qui rend l'ecart entre passes comparable a du
    /// bruit, et non a un changement de partie.</summary>
    public BancDeRejeu(List<string> fichiers)
    {
        _tranches = new List<List<double>>();
        var plusLongue = fichiers
            .Select(f => (f, n: File.ReadAllLines(f).Length))
            .OrderByDescending(x => x.n)
            .Select(x => x.f)
            .FirstOrDefault();
        if (plusLongue is null) return;

        var frames = File.ReadAllLines(plusLongue)
            .Select(l => double.TryParse(l, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => double.IsFinite(v) && v > 0).ToList();

        var cur = new List<double>();
        var t = 0.0;
        foreach (var ms in frames)
        {
            cur.Add(ms);
            t += ms;
            if (t < 60000) continue;
            _tranches.Add(cur);
            cur = new List<double>();
            t = 0;
        }

        Source = Path.GetFileName(plusLongue);
    }

    public string? Source { get; }
    public int Tranches => _tranches.Count;

    public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
    {
        if (_tranches.Count == 0)
            return Task.FromResult(Result<BenchmarkResult>.Fail(ErrorCodes.NoFrames, "aucune tranche"));

        var frames = _tranches[_passe++ % _tranches.Count];

        // NE PAS RECONSTRUIRE `BenchmarkResult` A LA MAIN ICI. Cette ligne etait positionnelle et
        // laissait `P95FrametimeMs` a son `= null` de contrat : `GameMetricSet.Valeur` rend alors 0,
        // `Juger` ecarte la grandeur, et le rejeu annoncait trois grandeurs en n'en jugeant que
        // deux — sans qu'aucune sortie ne le dise. `PasseDepuisSerie` porte les memes formules que
        // le banc de production, et c'est une garantie TESTEE.
        return Task.FromResult(Result<BenchmarkResult>.Ok(
            FrametimeStatistics.PasseDepuisSerie(frames, runs, marginOfError: 1.0,
                hygieneKeptFraction: 1.0)));
    }
}

/// <summary>Un candidat qui ne change RIEN. En rejeu l'ecriture n'aurait aucun sens, et un candidat
/// neutre exerce quand meme toute la chaine de decision — ce qui est precisement le but.</summary>
internal sealed class CandidatNeutre(string id = "controle (n'ecrit rien)") : IAutopilotCandidate
{
    public CandidateDescriptor Descriptor { get; } = new(
        id, CandidateKind.AutoHot, RiskLevel: 0, NeedsStability: false,
        new[] { "fps.moyen", "fps.p1low" });

    public Result Apply() => Result.Ok();
    public Result Revert() => Result.Ok();
    public Result<bool> ReadBack() => Result<bool>.Ok(true);
}
