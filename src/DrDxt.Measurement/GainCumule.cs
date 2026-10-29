using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>Un gain prouve par un instrument nomme.</summary>
/// <param name="Levier">Ce qui a produit ce gain.</param>
/// <param name="Instrument">L'instrument qui l'a mesure : « banc GPU », « banc CPU soutenu »,
/// « frametime en jeu ». C'est lui qui decide si deux gains sont additionnables.</param>
/// <param name="EcartPercent">L'ecart mesure, en pourcent. Peut etre negatif.</param>
/// <param name="BruitPercent">Le plancher de l'instrument pour cette mesure : dispersion, derive,
/// ou etendue des temoins. Un gain plus petit que lui n'est pas un gain.</param>
public sealed record GainProuve(string Levier, string Instrument, double EcartPercent, double BruitPercent);

/// <summary>Ce que l'addition de plusieurs gains autorise a dire.</summary>
/// <param name="TotalPercent">Le total compose, ou 0 si rien n'est additionnable.</param>
/// <param name="BruitPercent">Le bruit du total, compose lui aussi.</param>
/// <param name="Instrument">L'instrument commun aux gains additionnes.</param>
/// <param name="Retenus">Les leviers effectivement additionnes.</param>
/// <param name="Refus">Pourquoi les autres ne le sont pas. Vide quand tout a ete retenu.</param>
public sealed record TotalDeGains(
    double TotalPercent,
    double BruitPercent,
    string? Instrument,
    IReadOnlyList<string> Retenus,
    IReadOnlyList<string> Refus);

/// <summary>
/// Additionner des gains sans mentir (le « Cumulative game gain » du mandat « PERFORMANCE first »), et refuser quand
/// c'est impossible. Trois fautes rendues impossibles :
/// 1. Additionner des pourcentages : 2 % et 3 % font 1,02 x 1,03 - 1 = 5,06 % ; cinq leviers a 10 % font 61 %, pas 50.
/// 2. Melanger les instruments : 2,2 % au banc GPU et 10 % au banc CPU ne mesurent pas la meme chose ; « +12,4 % » n'a
///    aucun referent. Seul ce qui partage un instrument s'additionne, le reste est refuse et nomme.
/// 3. Composer le gain et additionner le bruit : le bruit d'un produit se propage en relatif. Sur cinq leviers a
///    +10 % ± 1 %, la formule en points dit ± 2,24 %, la propagation ± 3,27 % (Monte-Carlo, 400 000 tirages) : sous-
///    estimer l'incertitude surestime la confiance. Sur un seul gain les deux coincident (+2,2 % ± 0,122 % au banc GPU).
/// Un gain de banc n'est jamais un gain de jeu : le banc GPU resout 0,122 %, le jeu en ligne rend ±45 % sur le 1 % low.
/// </summary>
public static class GainCumule
{
    /// <summary>Compose les gains qui partagent l'instrument le plus represente, et dit pourquoi
    /// les autres sont ecartes.</summary>
    public static TotalDeGains Additionner(IReadOnlyList<GainProuve>? gains)
    {
        if (gains is null || gains.Count == 0)
            return new TotalDeGains(0, 0, null, [], [Docteur.T("GC.AucunGainProuve")]);

        // L'instrument le plus represente : le total le plus etaye.
        var instrument = gains
            .GroupBy(g => g.Instrument, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;

        var refus = new List<string>();
        var retenus = new List<string>();
        var facteur = 1.0;
        var bruitRelatifCarre = 0.0;

        foreach (var g in gains)
        {
            if (!string.Equals(g.Instrument, instrument, StringComparison.OrdinalIgnoreCase))
            {
                refus.Add(Docteur.T("GC.Instrument", g.Levier, g.Instrument, instrument));
                continue;
            }

            // Un ecart ou un bruit non mesure (NaN, infini, bruit negatif) n'est ni un gain ni du bruit : compose, il rendrait le
            // total « NaN % ».
            if (!double.IsFinite(g.EcartPercent) || !double.IsFinite(g.BruitPercent) || g.BruitPercent < 0)
            {
                refus.Add(Docteur.T("GC.NonMesure", g.Levier));
                continue;
            }

            // Un ecart plus petit que le bruit de sa propre mesure n'est pas un gain.
            if (Math.Abs(g.EcartPercent) <= g.BruitPercent)
            {
                refus.Add(Docteur.T("GC.Bruit", g.Levier, g.EcartPercent, g.BruitPercent));
                continue;
            }

            var f = 1 + g.EcartPercent / 100.0;

            // Un levier qui coute 100 % ou plus annule le produit et son incertitude relative : une machine qui ne rend
            // plus d'images, pas une mesure de performance.
            if (f <= 0)
            {
                refus.Add(Docteur.T("GC.Annule", g.Levier, g.EcartPercent));
                continue;
            }

            facteur *= f;
            // Incertitudes relatives (bruit / facteur) independantes : en quadrature, reportees a l'echelle du total.
            bruitRelatifCarre += g.BruitPercent / f * (g.BruitPercent / f);
            retenus.Add(g.Levier);
        }

        return retenus.Count == 0
            ? new TotalDeGains(0, 0, instrument, [], refus)
            : new TotalDeGains(
                (facteur - 1) * 100.0,
                facteur * Math.Sqrt(bruitRelatifCarre),
                instrument, retenus, refus);
    }

    /// <summary>
    /// Ce qu'une campagne a laisse dans la machine, sur une grandeur. Seuls les <see cref="CandidateOutcome.Kept"/>
    /// comptent : un rejete a ete annule. Une grandeur a la fois : un levier garde pour ses FPS moyens qui degrade le
    /// 1 % low y entre avec son signe. Une preuve non concluante (sous le bruit, invalide) n'entre pas.
    /// </summary>
    public static TotalDeGains DepuisLaCampagne(
        IReadOnlyList<CandidateResult>? resultats, GameMetricKind metrique)
    {
        var instrument = GameMetricSet.Libelle(metrique);

        var gains = (resultats ?? [])
            .Where(r => r.Outcome == CandidateOutcome.Kept)
            .Select(r => (r.Id, Preuve: r.Metriques?.FirstOrDefault(m => m.Metrique == metrique)?.Preuve))
            .Where(x => x.Preuve is { } p && p.EstConcluante)
            .Select(x => new GainProuve(x.Id, instrument, x.Preuve!.Value.DeltaPct, x.Preuve.Value.ResolutionPct))
            .ToList();

        return gains.Count == 0
            // La raison ne repete pas l'instrument : `Phrase` le nomme deja.
            ? new TotalDeGains(0, 0, instrument, [],
                [Docteur.T("GC.AucunLevier")])
            : Additionner(gains);
    }

    /// <summary>
    /// Le verdict en une phrase. Le total se juge contre son propre bruit compose : +3 % ± 3,3 % ne prouve rien, meme si
    /// chaque levier avait prouve le sien.
    /// </summary>
    public static string Phrase(TotalDeGains total)
    {
        var ou = total.Instrument is { } i ? Docteur.T("GC.Sur", i) : "";

        if (total.Retenus.Count == 0)
            return Docteur.T("GC.Rien", ou, total.Refus.Count > 0 ? total.Refus[0] : Docteur.T("GC.AucunGain"));

        var leviers = string.Join(", ", total.Retenus);

        if (Math.Abs(total.TotalPercent) <= total.BruitPercent)
            return Docteur.T("GC.PasProuve", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers);

        return total.TotalPercent > 0
            ? Docteur.T("GC.Prouve", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers)
            : Docteur.T("GC.Regression", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers);
    }
}
