using DrDxt.Measurement.Memory;

// ─────────────────────────────────────────────────────────────────────────────────────────
// memprobe — le banc memoire du produit, lance a la main.
//
// Il n'a plus d'algorithme a lui : il appelle `MemoryBench`, celui que l'application embarque.
// Une sonde qui mesure autre chose que le produit valide un instrument que personne ne livre.
//
// Ce que ca rend mesurable, et qui ne l'etait pas : simple canal contre double canal (le debit
// double), et XMP off contre XMP on (2133 -> 3800, soit environ +78 %). Des deltas enormes
// devant le bruit -- la ou le meme changement se perd dans une moyenne de FPS.
//
// Usage :  dotnet run -c Release --project tools/memprobe -- [passes] [MT/s configures]
//          La frequence est facultative ; sans elle, les controles qui ont besoin d'un plafond
//          theorique ne peuvent pas s'evaluer, et le disent.
//          Protocole dans docs/INSTRUMENTS.md : machine AU REPOS, meme protocole avant et apres.
// ─────────────────────────────────────────────────────────────────────────────────────────

// 24/09/2026 — `--stabilite <Go> <secondes>` : le test memoire EN SESSION du produit, sans redemarrer.
if (args.Length > 0 && args[0] == "--stabilite")
{
    var go = args.Length > 1 && int.TryParse(args[1], out var g) ? g : 4;
    var sec = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 60;
    Console.WriteLine($"test memoire en session : {go} Go, {sec} s, {Environment.ProcessorCount} fils");
    var r = DrDxt.Measurement.Memory.TestMemoireEnSession.Lancer((long)go << 30, TimeSpan.FromSeconds(sec),
        Environment.ProcessorCount, CancellationToken.None);
    Console.WriteLine($"  teste {r.OctetsTestes / (1L << 20)} Mo, {r.Passes} passes, {r.Erreurs} erreur(s), {r.Duree.TotalSeconds:0} s");
    Console.WriteLine($"  {r.Phrase}");
    Environment.ExitCode = r.Erreurs == 0 ? 0 : 1;
    return;
}

int passes = args.Length > 0 && int.TryParse(args[0], out var p) && p > 0 ? p : 10;
int mts = args.Length > 1 && int.TryParse(args[1], out var f) && f > 0 ? f : 0;

Console.WriteLine($"memprobe — {passes} passes, machine attendue AU REPOS");
Console.WriteLine();

var m = MemoryBench.Measure(passes);

Console.WriteLine($"DEBIT    {m.BandwidthGBs,8:N2} GB/s   sigma/moyenne {m.BandwidthSigmaPct,5:N2} %");
Console.WriteLine($"LATENCE  {m.LatencyNs,8:N2} ns     sigma/moyenne {m.LatencySigmaPct,5:N2} %");
Console.WriteLine($"EN VOL   {MemoryInference.LinesInFlight(m.BandwidthGBs, m.LatencyNs),8:N1} lignes de cache (loi de Little)");
Console.WriteLine();

Console.WriteLine("ESCALIER DE CACHE");
foreach (var point in m.Staircase)
    Console.WriteLine($"  {point.WindowBytes / 1024,10:N0} Ko  {point.LatencyNs,7:N2} ns");

var sauts = CacheStaircase.FindJumps(m.Staircase);
Console.WriteLine(sauts.Count == 0
    ? "  sauts : AUCUN — la courbe ne decrit pas une hierarchie de caches"
    : "  sauts (PAS des frontieres de cache) : " + string.Join(" · ", sauts.Select(k => $"{k / 1024:N0} Ko")));
Console.WriteLine();

// ── CONTROLE POSITIF : la chaine voit-elle un vrai changement ? ──
//
// Un instrument qui n'a jamais VU un changement qu'on lui a fabrique n'est pas valide : il peut
// tres bien rendre toujours le meme nombre. Ici on fabrique une baisse de debit dont on connait
// le SENS (des fils concurrents qui saturent la memoire ne peuvent que ralentir la mesure), et on
// demande a MemoryVerdict -- le code que le produit livre -- de trancher.
//
// LECTURE PURE, et reversible : les fils s'arretent avec le programme. Aucune ecriture nulle part.
if (args.Contains("--controle-positif"))
{
    Console.WriteLine();
    Console.WriteLine("== CONTROLE POSITIF : mesurer, charger la memoire, remesurer ==");

    var avant = m;
    // Le nombre de fils se DOSE. Trop de charge et sigma explose : Q-03 refuse la mesure, ce
    // qui est correct mais rend le controle inutilisable. Le bon dosage bouge la moyenne bien
    // au-dela de 3 sigma sans agiter la machine au point qu'on ne puisse plus la mesurer.
    var filsArg = args.FirstOrDefault(a => a.StartsWith("--fils=", StringComparison.Ordinal));
    int fils = filsArg is not null && int.TryParse(filsArg.AsSpan(7), out var f2) && f2 > 0 ? f2 : 2;

    var stop = new CancellationTokenSource();
    var charges = new List<Thread>();
    for (int i = 0; i < fils; i++)
    {
        var t = new Thread(() => Streamer(stop.Token)) { IsBackground = true };
        t.Start();
        charges.Add(t);
    }

    Console.WriteLine($"   {charges.Count} fils de charge memoire lances, remesure en cours...");
    var apres = MemoryBench.Measure(passes);
    stop.Cancel();

    Console.WriteLine($"   AVANT {avant.BandwidthGBs,7:N2} GB/s  (sigma {avant.BandwidthSigmaPct:N2} %)");
    Console.WriteLine($"   APRES {apres.BandwidthGBs,7:N2} GB/s  (sigma {apres.BandwidthSigmaPct:N2} %)");

    var plafond = mts > 0 ? mts * 8.0 / 1000.0 * 2 : double.MaxValue;
    var controle = MemoryVerdict.Compare(avant, apres, plafond);
    Console.WriteLine(controle.HasVerdict
        ? $"   VERDICT  debit {controle.BandwidthDeltaPct:+0.0;-0.0} %  "
          + (controle.BandwidthSignificant ? "AU-DELA DE 3 SIGMA : la chaine a VU le changement"
                                          : "sous 3 sigma : la chaine ne se prononce pas")
        : $"   VERDICT  aucun -- {controle.NoVerdictReason}");
    Console.WriteLine();
    Console.WriteLine("   Attendu : un delta NEGATIF et significatif. Un delta positif, ou un");
    Console.WriteLine("   'ne se prononce pas', voudrait dire que l'instrument ne voit pas ce qui");
    Console.WriteLine("   se passe sous son nez -- et alors aucun de ses OUI ne vaut rien.");
    return;
}

static void Streamer(CancellationToken ct)
{
    // 64 Mo par fil : assez pour deborder du cache et disputer vraiment la memoire.
    var buffer = new long[8 * 1024 * 1024];
    long sink = 0;
    while (!ct.IsCancellationRequested)
    {
        for (int i = 0; i < buffer.Length; i += 8) sink += buffer[i];
        if (sink == long.MinValue) Console.Write(' ');
    }
}

// ── Le self-check. Il n'est pas decoratif : c'est lui qui peut dire NON. ──
if (mts == 0)
{
    Console.WriteLine("SELF-CHECK  non evalue : donne les MT/s configures en 2e argument");
    Console.WriteLine("            (ex. dotnet run -c Release --project tools/memprobe -- 10 3800)");
    return;
}

// Sans inventaire materiel ici, on majore avec le nombre de barrettes le plus courant : deux.
// Le plafond ainsi calcule est celui que le produit donne a Q-01 via MemoryInference.
double perChannel = mts * 8.0 / 1000.0;
double upperBound = perChannel * 2;

var verdict = MemoryQuality.Check(m, upperBound);
Console.WriteLine($"SELF-CHECK  plafond {upperBound:N1} GB/s ({mts} MT/s x 8 o x 2)");
Console.WriteLine(verdict.Accepted
    ? "            ACCEPTEE — les quatre controles passent"
    : $"            REJETEE — {verdict.RejectionId}");

double ratio = m.BandwidthGBs / perChannel;
Console.WriteLine($"            {ratio:N2} x le plafond d'UN canal");
Console.WriteLine($"            canaux PROUVES : au moins {(int)Math.Max(1, Math.Floor(ratio / 1.02) + 1)}");
Console.WriteLine();
Console.WriteLine("Un ecart plus petit que 3 sigma n'est pas un resultat : c'est du bruit qui a");
Console.WriteLine("l'air d'un gain. La regle vit dans MemoryVerdict, pas dans la tete du lecteur.");
