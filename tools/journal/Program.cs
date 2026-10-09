using DrDxt.Core;
using DrDxt.Measurement;

// « MON PC EST-IL PLUS LENT QU'AVANT ? » — ce que le journal repond, sans passer par l'ecran.
//
// LECTURE SEULE, et ce n'est pas une precaution de style : ajouter une entree depuis un outil
// polluerait l'historique du client avec une mesure qui n'est pas une session de jeu. Le journal
// ne se remplit que par un scan.
//
//   dotnet run --project tools/journal [chemin]
//
// Sans argument, il lit le journal reel de cette machine.

var chemin = args.Length > 0 ? args[0] : FileSystemPerformanceJournalFile.DefaultPath();
Console.WriteLine($"== journal de performance : {chemin} ==");

if (!File.Exists(chemin))
{
    // Un journal absent n'est pas une panne : c'est un produit qui n'a pas encore fini un scan
    // avec une mesure exploitable. Le dire evite de conclure que la lecture a echoue.
    Console.WriteLine("   AUCUN JOURNAL. Il naît au premier scan qui rend une mesure en jeu :");
    Console.WriteLine("   sans jeu lance, le banc refuse et rien n'est enregistre.");
    return 0;
}

var entrees = new PerformanceJournalStore(new FileSystemPerformanceJournalFile(chemin)).Lire();
Console.WriteLine($"   {entrees.Count} mesure(s) retenue(s).");
foreach (var e in entrees.OrderBy(e => e.TakenUtc))
    Console.WriteLine($"   {e.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {e.MachineId}  "
                    + $"avg={e.AvgFps:0.#} fps  marge=±{e.MarginOfError:0.##}  passes={e.Runs}  "
                    + $"exposition={(e.KeptFraction is { } k ? k.ToString("P0") : "non lue")}  "
                    // Le JEU se lit ici, sinon un refus de comparer reste incomprehensible.
                    + $"jeu={e.GameId ?? "non identifie"}");

var preuve = PerformanceJournal.Derive(entrees).Game?.Preuve;
Console.WriteLine();
if (preuve is not { } p)
{
    Console.WriteLine("   VERDICT : rien a comparer — moins de deux mesures du MEME jeu sur la "
                    + "MEME machine, ou un jeu non identifie. Le produit se tait.");
    return 0;
}

Console.WriteLine(p.Outcome switch
{
    EvidenceOutcome.ProvenRegression =>
        $"   VERDICT : REGRESSION PROUVEE — {p.DeltaPct:0.#} % contre une resolution de "
        + $"±{p.ResolutionPct:0.#} %.",
    EvidenceOutcome.ProvenGain =>
        $"   VERDICT : GAIN PROUVE — +{p.DeltaPct:0.#} % contre une resolution de "
        + $"±{p.ResolutionPct:0.#} %.",
    EvidenceOutcome.Invalid =>
        $"   VERDICT : comparaison refusee — {p.InvalidReason}. Aucun chiffre n'en sort.",
    _ =>
        $"   VERDICT : rien de resolu — l'ecart mesure ({p.DeltaPct:0.#} %) reste sous la "
        + $"resolution (±{p.ResolutionPct:0.#} %). C'est une BORNE, pas une stabilite prouvee.",
});
return 0;
