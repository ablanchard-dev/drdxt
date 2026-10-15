namespace DrDxt.Measurement.Memory;

/// <summary>
/// Le resultat d'un A/B memoire. <see cref="HasVerdict"/> faux veut dire « on ne se prononce
/// pas », ce qui n'est ni un gain ni une absence de gain : c'est l'aveu qu'une des deux mesures
/// n'a pas ete jugee valide.
/// </summary>
public sealed record MemoryAbVerdict(
    bool HasVerdict,
    string? NoVerdictReason,
    double BandwidthDeltaPct,
    double LatencyDeltaPct,
    bool BandwidthSignificant,
    bool LatencySignificant,
    // Les valeurs brutes voyagent avec le verdict : un ecran qui annonce « +79 % » sans montrer
    // 19,1 -> 34,2 demande de le croire sur parole. Le pourcentage se recalcule a la main.
    double BandwidthBeforeGBs = 0,
    double BandwidthAfterGBs = 0,
    double LatencyBeforeNs = 0,
    double LatencyAfterNs = 0,
    // La resolution : le plus petit ecart que ces deux mesures pouvaient distinguer, en pourcent.
    // Sans elle, « pas significatif » se lit « ca ne sert a rien », ce qui est une conclusion que
    // l'instrument n'a pas produite. Mesure du 17/09/2026 : sur cette machine la resolution du
    // debit vaut ~7,8 %, quand passer de 3 800 a 4 000 MT/s n'en promet que 5,3 : le banc ne peut
    // pas trancher cette question-la, et c'est ce chiffre qui le dit. L'ecran de scan parle
    // deja ainsi pour le GPU (Scan.Drift.None). NaN quand il n'y a pas de verdict.
    double BandwidthResolutionPct = double.NaN,
    double LatencyResolutionPct = double.NaN);

/// <summary>
/// La comparaison avant / apres d'un changement memoire, et le seul endroit du produit autorise a
/// dire « ca a change quelque chose ».
///
/// La regle : un ecart ne compte que s'il depasse 3 sigma de la difference des deux mesures. En
/// dessous, la phrase est « aucune difference mesurable », jamais « leger gain ». C'est ce
/// garde-fou qui separe un instrument d'un generateur d'espoir.
/// </summary>
public static class MemoryVerdict
{
    /// <summary>
    /// Nombre d'ecarts-types exiges. Choix produit : a 3 sigma, la probabilite qu'un ecart de
    /// cette taille soit du bruit est inferieure a 1 %. Deux sigma laisserait passer un ecart
    /// sur vingt : trop, pour une phrase que le client lit comme une preuve.
    /// </summary>
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

    /// <summary>
    /// Le seuil, en pourcent. Les deux mesures apportent chacune leur dispersion, et l'incertitude
    /// d'une difference est la racine de la somme des carres, pas la somme, qui surestimerait.
    /// On garde les sigma des passes et non ceux de la moyenne : c'est plus severe, et sur une
    /// phrase que le client lira comme une preuve, l'erreur a ne pas commettre est de crier un
    /// gain qui n'existe pas.
    /// </summary>
    private static double Threshold(double sigmaBeforePct, double sigmaAfterPct)
        => SigmaFactor * Math.Sqrt(sigmaBeforePct * sigmaBeforePct + sigmaAfterPct * sigmaAfterPct);

    private static double DeltaPct(double before, double after)
        => before <= 0 ? 0 : (after - before) / before * 100.0;

    private static MemoryAbVerdict NoVerdict(string reason)
        => new(false, reason, 0, 0, false, false);
}
