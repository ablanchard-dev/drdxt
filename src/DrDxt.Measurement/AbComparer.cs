using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Verdict A/B. Le critère de régression est d'abord la régularité des images (1 % lows), pas
/// les seuls FPS moyens :
///   • Régression (→ retour automatique) quand les 1 % lows empirent au-delà de la marge
///     combinée (régularité dégradée) ou que les FPS moyens empirent au-delà de la marge combinée.
///     La régularité est le déclencheur principal : elle passe avant un léger mouvement des FPS
///     moyens qui reste dans la marge.
///   • Gain mesurable quand les 1 % lows ou les FPS moyens s'améliorent au-delà de la marge
///     combinée (et sans régression).
///   • Sinon, aucun gain mesurable, y compris l'issue clé d'un levier d'efficacité : FPS moyens
///     dans la marge et 1 % lows tenus ou améliorés (un undervolt est à ~0 sur les FPS moyens par
///     nature). C'est un état gardé valide, jamais défait automatiquement.
/// La latence (EstimatedPcLatencyMs) reste informative : ce n'est pas un déclencheur de retour automatique.
/// </summary>
public static class AbComparer
{
    /// <summary>La clé d'un verdict de régression : le moteur la reconnaît pour retirer le lot.</summary>
    public const string CleRegression = "Scan.Ab.Regression";

    /// <summary>Ecart maximal d'exposition entre deux passes A/B avant que la comparaison ne soit
    /// declaree invalide : dix points de trames gardees.
    ///
    /// **Non chiffré** : ce seuil n'est pas mesure, et il est ecrit ici pour etre trouve et
    /// discute plutot que dilue dans une condition. Ce qui est mesure l'encadre :
    /// mesure le 07/09/2026 sur 2 170 secondes de jeu reel, l'hygiene retire **0 %**, donc deux passes propres
    /// valent toutes deux 1,0 et sont a zero d'ecart. Dix points laissent passer une seconde de
    /// chargement d'un cote et pas de l'autre, et arretent un alt-tab. La direction du doute est
    /// choisie : mieux vaut refuser une comparaison douteuse que publier un gain fabrique par une
    /// difference de session.</summary>
    public const double ExpositionComparableMax = 0.10;

    public static AbVerdict Compare(BenchmarkResult before, BenchmarkResult after)
    {
        // Deux instruments différents : l'écart dirait l'instrument (voir Evidence), ni gain ni régression.
        if (!string.Equals(before.Instrument ?? "PresentMon", after.Instrument ?? "PresentMon", StringComparison.OrdinalIgnoreCase))
            return new AbVerdict(before, after, false, "Scan.Ab.NoMeasurableGain");

        var combinedMargin = before.MarginOfError + after.MarginOfError;
        // Les lows se jugent avec leur marge, pas celle des FPS moyens (plus etroite).
        var combinedLowMargin = before.P1LowMarginOfError + after.P1LowMarginOfError;

        var avgDelta = after.AvgFps - before.AvgFps;
        var lowDelta = after.P1LowFps - before.P1LowFps;

        // La régularité (1 % lows) est le signal principal ; une régression des 1 % lows au-delà de la
        // marge l'emporte même si les FPS moyens ont un peu monté dans la marge. Une perte de FPS moyens
        // au-delà de la marge est aussi une régression.
        var lowsRegressed = lowDelta < -combinedLowMargin;
        var avgRegressed = avgDelta < -combinedMargin;

        // Un gain sur les 1 % lows ou sur les FPS moyens, au-delà de la marge.
        var lowsGained = lowDelta > combinedLowMargin;
        var avgGained = avgDelta > combinedMargin;

        string key;
        bool gain;
        if (lowsRegressed || avgRegressed)
        {
            gain = false;
            key = CleRegression;
        }
        else if (lowsGained || avgGained)
        {
            gain = true;
            key = "Scan.Ab.MeasurableGain";
        }
        else
        {
            gain = false;
            key = "Scan.Ab.NoMeasurableGain";
        }

        return new AbVerdict(before, after, gain, key);
    }

    /// <summary>
    /// Le meme a/b, mais qui garde sa resolution.
    ///
    /// <see cref="Compare"/> calcule `combinedMargin`, s'en sert, et la jette : elle vit
    /// dans une variable locale et meurt avec la methode. C'est pourtant elle qui distingue
    /// « aucun gain » de « gain non resolu par cette session ». Sur la machine de reference, cette
    /// confusion fait passer un +2,7 % reel pour un zero, alors que la session ne resout que +/-4,8 %.
    ///
    /// Rend une <see cref="GameEvidence"/> sur les FPS moyens : l'ecart en pourcent et ce que
    /// l'experience savait resoudre, cote a cote. Le sous-systeme reste null : c'est au banc
    /// deterministe de le remplir, avec sa resolution a lui, bien plus fine.
    ///
    /// La resolution est la somme des deux marges, comme dans <see cref="Compare"/>. La chaine
    /// memoire, elle, utilise la racine de la somme des carres. La somme est plus large, donc plus
    /// prudente ; les aligner serait changer un seuil, et un seuil ne se change pas sans mesure.
    /// </summary>
    public static PerformanceEvidence Evidence(BenchmarkResult before, BenchmarkResult after)
    {
        // Pas de reference = pas de pourcentage. Un ecart relatif a zero est infini, donc un
        // verdict tire au sort. Ce n'est pas « pas de changement » : c'est une experience qui ne
        // tient pas, et les deux ne se confondent jamais.
        if (!(before.AvgFps > 0) || !double.IsFinite(before.AvgFps) || !double.IsFinite(after.AvgFps))
            return new PerformanceEvidence(
                null,
                new GameEvidence("fps.moyen",
                    Core.Evidence.Invalide(Docteur.T("AB.SansReference"))));

        // Deux instruments ne comptent pas la meme chose : le repli DXGI ne passe ni par l'hygiene ni par
        // l'exposition. Un ecart entre PresentMon et DXGI dirait l'instrument, pas le reglage.
        if (!string.Equals(before.Instrument ?? "PresentMon", after.Instrument ?? "PresentMon", StringComparison.OrdinalIgnoreCase))
            return new PerformanceEvidence(
                null,
                new GameEvidence("fps.moyen", Core.Evidence.Invalide(
                    Docteur.T("AB.Instruments", before.Instrument ?? "PresentMon", after.Instrument ?? "PresentMon"))));

        // Les deux passes ont-elles vecu la meme chose ? Sans cette question, un A/B compare
        // deux sessions differentes et rend un verdict sans broncher. Le cas concret : l'hygiene
        // retire 2 % de la passe avant et 45 % de la passe apres parce que le joueur a alt-tabbe.
        // C'est observe, pas infere : l'exposition vient du masque d'hygiene, pas d'une heuristique.
        if (before.HygieneKeptFraction is { } avant && after.HygieneKeptFraction is { } apres
            && Math.Abs(avant - apres) > ExpositionComparableMax)
            return new PerformanceEvidence(
                null,
                new GameEvidence("fps.moyen", Core.Evidence.Invalide(
                    Docteur.T("AB.Exposition", avant, apres))));

        var deltaPct = (after.AvgFps - before.AvgFps) / before.AvgFps * 100.0;
        var resolutionPct = (before.MarginOfError + after.MarginOfError) / before.AvgFps * 100.0;

        return new PerformanceEvidence(
            null,
            new GameEvidence("fps.moyen", Core.Evidence.From(deltaPct, resolutionPct)));
    }
}
