namespace DrDxt.Measurement.Memory;

/// <summary>Le resultat d'un A/B memoire. <see cref="HasVerdict"/> faux = « on ne se prononce pas » (une des deux mesures
/// n'a pas ete jugee valide), ni gain ni absence de gain.</summary>
public sealed record MemoryAbVerdict(
    bool HasVerdict,
    string? NoVerdictReason,
    double BandwidthDeltaPct,
    double LatencyDeltaPct,
    bool BandwidthSignificant,
    bool LatencySignificant,
    // Les valeurs brutes voyagent avec le verdict : « +79 % » se verifie sur 19,1 -> 34,2.
    double BandwidthBeforeGBs = 0,
    double BandwidthAfterGBs = 0,
    double LatencyBeforeNs = 0,
    double LatencyAfterNs = 0,
    // La resolution, en pourcent : le plus petit ecart distinguable, sans quoi « pas significatif » se lirait « ca ne
    // sert a rien ». Mesure du 17/09/2026 : resolution du debit ~7,8 % sur cette machine, quand 3 800 → 4 000 MT/s n'en
    // promet que 5,3. Comme Scan.Drift.None pour le GPU. NaN sans verdict.
    double BandwidthResolutionPct = double.NaN,
    double LatencyResolutionPct = double.NaN);

/// <summary>La comparaison avant / apres d'un changement memoire, le seul endroit autorise a dire « ca a change quelque
/// chose » : un ecart ne compte qu'au-dela de 3 sigma de la difference ; en dessous, « aucune difference mesurable »,
/// jamais « leger gain ».</summary>
public static class MemoryVerdict
{
    /// <summary>Nombre d'ecarts-types exiges. Choix produit : a 3 sigma, moins de 1 % de chances que l'ecart soit du
    /// bruit ; 2 sigma en laisserait passer un sur vingt.</summary>
    public const double SigmaFactor = 3.0;

    public static MemoryAbVerdict Compare(
        MemoryMeasurement before, MemoryMeasurement after, double theoreticalPeakGBs)
    {
        var qa = MemoryQuality.Check(before, theoreticalPeakGBs);
        if (!qa.Accepted)
            return NoVerdict($"{qa.RejectionId} (avant)");

        var qb = MemoryQuality.Check(after, theoreticalPeakGBs);
        if (!qb.Accepted)
            return NoVerdict($"{qb.RejectionId} (apres)");

        var bandwidthDelta = DeltaPct(before.BandwidthGBs, after.BandwidthGBs);
        var latencyDelta = DeltaPct(before.LatencyNs, after.LatencyNs);

        return new MemoryAbVerdict(
            HasVerdict: true,
            NoVerdictReason: null,
            BandwidthDeltaPct: bandwidthDelta,
            LatencyDeltaPct: latencyDelta,
            BandwidthSignificant: Math.Abs(bandwidthDelta)
                > Threshold(before.BandwidthSigmaPct, after.BandwidthSigmaPct),
            LatencySignificant: Math.Abs(latencyDelta)
                > Threshold(before.LatencySigmaPct, after.LatencySigmaPct),
            BandwidthBeforeGBs: before.BandwidthGBs,
            BandwidthAfterGBs: after.BandwidthGBs,
            LatencyBeforeNs: before.LatencyNs,
            LatencyAfterNs: after.LatencyNs,
            BandwidthResolutionPct: Threshold(before.BandwidthSigmaPct, after.BandwidthSigmaPct),
            LatencyResolutionPct: Threshold(before.LatencySigmaPct, after.LatencySigmaPct));
    }

    /// <summary>Le seuil, en pourcent : l'incertitude d'une difference est la racine de la somme des carres des deux
    /// dispersions (la somme surestimerait). Sigma des passes, pas de la moyenne : plus severe, contre un gain crie a
    /// tort.</summary>
    private static double Threshold(double sigmaBeforePct, double sigmaAfterPct)
        => SigmaFactor * Math.Sqrt(sigmaBeforePct * sigmaBeforePct + sigmaAfterPct * sigmaAfterPct);

    private static double DeltaPct(double before, double after)
        => before <= 0 ? 0 : (after - before) / before * 100.0;

    private static MemoryAbVerdict NoVerdict(string reason)
        => new(false, reason, 0, 0, false, false);
}
