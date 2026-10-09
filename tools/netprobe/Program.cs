using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Detection.Wmi;
using DrDxt.Measurement.Network;

// NS-9 live smoke: runs the REAL instrument (SystemPingProbe + NetworkLatencyMeter) against
// a real host and prints the report. A pure unit test proves the arithmetic; only this proves
// the ICMP path actually answers on this machine.
//
//   dotnet run --project tools/netprobe [host] [probeCount] [intervalMs]

// 24/09/2026 — `--envoi <secondes>` : le bufferbloat MONTANT, juge sur le trafic reel (rien envoye
// hors ping). Lance ton envoi (stream, sauvegarde) pendant la fenetre.
if (args.Length > 0 && args[0] == "--envoi")
{
    var secondes = args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 60;
    var e = await new CollecteurEnvoi(new SystemPingProbe(), "1.1.1.1")
        .CollecterAsync(TimeSpan.FromSeconds(secondes), TimeSpan.FromMilliseconds(250), CancellationToken.None);
    var v = Bufferbloat.Juger(e);
    Console.WriteLine("plus forts : " + string.Join(" | ", e.OrderByDescending(x => x.EnvoiMbps).Take(6).Select(x => $"{x.EnvoiMbps:0} Mbit/s {(x.PingMs is { } p ? p.ToString("0") : "-")} ms")));
    Console.WriteLine($"echantillons={e.Count}  envoi max={v.CapaciteMbps:0} Mbit/s");
    Console.WriteLine($"repos  : n={v.NRepos} p90={v.PingReposP90?.ToString("0") ?? "-"} ms");
    Console.WriteLine($"charge : n={v.NCharge} p90={v.PingChargeP90?.ToString("0") ?? "-"} ms");
    Console.WriteLine($"verdict: {(v.Detecte ? "BUFFERBLOAT" : "rien")} — {v.Raison}; plafond conseille {v.PlafondConseilleBps / 1_000_000} Mbit/s");
    return 0;
}

string host = args.Length > 0 ? args[0] : "1.1.1.1";
int count = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 10;
int intervalMs = args.Length > 2 && int.TryParse(args[2], out var i) ? i : 200;

Console.WriteLine($"== NS-9 network latency instrument (ICMP, read-only) ==");
Console.WriteLine($"   target={host}  probes={count}  interval={intervalMs}ms");

var meter = new NetworkLatencyMeter(new SystemPingProbe());
var r = await meter.MeasureAsync(host, count, TimeSpan.FromMilliseconds(intervalMs),
    TimeSpan.FromSeconds(2), CancellationToken.None);

if (!r.IsOk)
{
    Console.WriteLine($"RESULT: refused — {r.Error?.Code}: {r.Error?.Message}");
    return 1;
}

var rep = r.Value!;
Console.WriteLine($"   sent={rep.Sent}  received={rep.Received}  loss={rep.LossPercent:0.#}%");
Console.WriteLine(rep.HasLatency
    ? $"   avg={rep.AvgMs:0.##} ms   min={rep.MinMs:0.##}   max={rep.MaxMs:0.##}   p95={rep.P95Ms:0.##}   jitter={rep.JitterMs:0.##} ms"
    : "   latency = UNKNOWN (nothing came back — no number is printed rather than a fake 0 ms)");
Console.WriteLine($"   caveat[{rep.CaveatKey}]: ICMP round-trip to ONE host. NOT the game server, "
                + "NOT click-to-photon, and ICMP loss is NOT game packet loss.");

// Adapter state + guidance (read-only; there is no network apply path by decision).
Console.WriteLine();
Console.WriteLine("== NS-9 adapter read (connected physical adapters only) ==");
var reader = new NetworkAdapterReader(new ManagementWmiQuery());
var nics = reader.Read();
if (!nics.IsOk)
{
    Console.WriteLine($"   read failed — {nics.Error?.Code}: {nics.Error?.Message}");
    return 1;
}
foreach (var nic in nics.Value!)
    Console.WriteLine($"   {nic.Name} [{nic.Description}] wired={nic.IsWired}  "
                    + $"*InterruptModeration={nic.InterruptModeration}  *RSS={nic.Rss}  *EEE={nic.EnergyEfficientEthernet}");

// Attribution : exactement ce que le PRODUIT dit maintenant, par le meme chemin que l'ecran.
// L'outil ne refait plus le raisonnement dans son coin : il appelle le service, et ce qui
// s'imprime ici est la phrase que le client lit apres un scan — ou rien du tout.
Console.WriteLine();
Console.WriteLine($"== attribution (chemin produit) : 1er saut vs {host} ==");
var ligne = await new AttributionReseauService(reader, meter, host).LigneAsync(CancellationToken.None);
Console.WriteLine(ligne is null
    ? "   (rien a attribuer — pas de passerelle lisible, une fenetre sans reponse, ou un verdict "
      + "indecidable. Le produit se tait plutot que de rassurer.)"
    : "   " + ligne);

var advice = NetworkAdvisor.Advise(nics.Value!);
Console.WriteLine(advice.Count == 0
    ? "   guidance: nothing to say (every setting read is already where it should be)."
    : "   guidance (never auto-applied):");
foreach (var g in advice)
    Console.WriteLine($"     - [{g.Id}] {g.Adapter} -> {g.MessageKey}  ({g.SourceUrl})");

// Ce que le SCAN dirait vraiment : le chemin complet lecture -> SettingsState -> findings.
Console.WriteLine();
Console.WriteLine("== ce que le scan remonte (chemin produit complet) ==");
var state = new SettingsState(TriState.Unknown, TriState.Unknown,
    KnownPowerPlans.Balanced.ToString(), TriState.Unknown,
    // GameMode est passe de bool a TriState ("illisible" n'est pas "desactive") et cette sonde
    // etait restee en bool : elle ne compilait plus. Unknown est le bon defaut ici -- la sonde ne
    // lit pas ce reglage, et ce chemin ne sert qu'aux findings reseau.
    GameMode: TriState.Unknown,
    ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.Unknown,
    NetworkAdapters: nics.Value);
var hw = new HardwareInventory(
    new CpuInfo("(sonde)", "Intel", 1, 1, IsX3D: false, CcdCount: 1),
    Array.Empty<GpuInfo>(), Array.Empty<MemoryModule>(),
    new MotherboardInfo("(sonde)", "(sonde)"), new BiosInfo("0", null, true), ChassisType.Desktop);
foreach (var f in DrDxt.Detection.Scoring.ScoringRules.Evaluate(hw, state)
                     .Where(f => f.TitleKey.StartsWith("Finding.Network.")))
    Console.WriteLine($"   [{f.Severity}] {f.TitleKey}  gain={(f.EstimatedGainPct?.ToString() ?? "aucun chiffre")}");
return 0;
