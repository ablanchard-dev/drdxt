namespace DrDxt.Core;

/// <summary>Une experience de controle : un candidat qui n'ecrit rien, passe dans toute la chaine
/// de decision. L'effet vrai y vaut zero par construction : tout ecart trouve est une erreur, et
/// la resolution annoncee est censee le couvrir.</summary>
/// <param name="QuandUtc">Quand. Ce qui vieillit doit pouvoir etre date.</param>
/// <param name="Scene">Ce qui etait joue. Une « zone calme » et une fin de partie ne se
/// comparent pas, et melanger les deux ferait une calibration moyenne de rien.</param>
/// <param name="SecondesParPasse">Le protocole compte : la resolution en depend.</param>
/// <param name="Passes">Combien de passes d'alternance l'experience a consommees.</param>
/// <param name="FocusMinimal">La plus basse fraction de premier plan observee. Une observation
/// prise pendant un alt-tab ne calibre rien.</param>
/// <param name="Metrique">Quelle grandeur. Chacune se calibre separement.</param>
/// <param name="DeltaPct">Ce que l'experience a trouve. Devrait tourner autour de zero.</param>
/// <param name="ResolutionPct">Ce qu'elle a annonce savoir resoudre.</param>
public sealed record NoOpObservation(
    DateTimeOffset QuandUtc,
    string Scene,
    double SecondesParPasse,
    int Passes,
    double? FocusMinimal,
    GameMetricKind Metrique,
    double DeltaPct,
    double ResolutionPct)
{
    /// <summary>L'intervalle a-t-il rate ? Sur un no-op, un intervalle a 95 % doit exclure zero
    /// environ une fois sur vingt. Beaucoup plus souvent = le produit est surconfiant.</summary>
    public bool Depassement =>
        double.IsFinite(DeltaPct) && double.IsFinite(ResolutionPct)
        && Math.Abs(DeltaPct) > ResolutionPct;
}

/// <summary>Ce que la calibration etablit sur une grandeur.</summary>
public enum CalibrationVerdict
{
    /// <summary>Pas assez d'observations pour dire quoi que ce soit. Ce n'est pas
    /// « calibre », c'est « on ne sait pas encore ».</summary>
    PasAssezDObservations,

    /// <summary>Le taux de depassement est compatible avec les 5 % attendus.</summary>
    Calibre,

    /// <summary>L'intervalle exclut zero bien plus souvent que 5 % sur un no-op. Le produit
    /// annonce une precision qu'il n'a pas et finira par garder un reglage inerte.</summary>
    Surconfiant,

    /// <summary>L'intervalle est beaucoup plus large que le bruit reellement observe. Aucun faux
    /// verdict n'en sortira, mais le produit est aveugle a des gains reels.</summary>
    SousConfiant,
}

/// <summary>Ce que les no-op accumules disent du modele, avec de quoi le contester.</summary>
public sealed record CalibrationRapport(
    GameMetricKind Metrique,
    int Observations,
    int Depassements,
    double TauxDepassementPct,
    double EcartP95Pct,
    double ResolutionMedianePct,
    CalibrationVerdict Verdict);

/// <summary>
/// Le no-op est notre verite terrain.
///
/// Un modele statistique est joli ou il ne l'est pas ; ce qui compte est que ses intervalles
/// correspondent au bruit reel de cette machine, dans ce jeu, sur cette scene. Un candidat qui
/// n'ecrit rien donne exactement cette reference : l'effet y vaut zero par construction, donc
///
///   * l'ecart trouve mesure directement l'erreur de la chaine complete ;
///   * un intervalle a 95 % doit exclure zero **environ une fois sur vingt**, pas plus.
///
/// C'est la seule facon de detecter la surconfiance. Elle ne garde pas un keep : seul l'outil de campagne
/// (`tools/optimize`, par NoOpCalibrationStore) la calcule et l'affiche, et le moteur ne la consulte pas avant de garder. Une
/// formule mathematiquement correcte peut etre surconfiante en pratique : il suffit que les
/// passes ne soient pas aussi independantes qu'elle le suppose, ce qui est le cas normal en jeu.
/// Mesure du 07/09/2026 sur le corpus des 8 captures Warzone : le p99 frametime annoncait 9,2 %
/// pour un ecart p95 observe de 15,3 %, exactement ce piege.
///
/// Le verdict inverse compte tout autant. Un produit dont les intervalles sont trois fois trop
/// larges ne se trompera jamais **et ne verra jamais rien** : c'etait l'etat de DrDXT, qui
/// annoncait 14,3 % pour un ecart p95 observe de 3,9 % sur le corpus.
///
/// Pur, aucune I/O : la persistance vit ailleurs, cette logique se teste sans disque.
/// </summary>
public static class NoOpCalibration
{
    /// <summary>Le taux de depassement attendu d'un intervalle a 95 %.</summary>
    public const double TauxAttendu = 0.05;

    /// <summary>En dessous, la binomiale est trop plate pour separer 5 % de 15 % : le produit doit
    /// dire « pas encore » plutot que de trancher sur trois tirages.
    ///
    /// **Non chiffré.** A 20 observations, le test exact ci-dessous ne detecte une surconfiance
    /// qu'a partir de 4 depassements (P(X >= 4) = 0,016 ; 3 donnent 0,075) : il faut donc que le modele soit franchement mauvais
    /// pour etre attrape ici. C'est assume : mieux vaut manquer une petite surconfiance que la
    /// declarer sur du bruit.</summary>
    public const int ObservationsMinimum = 20;

    /// <summary>Juge une grandeur sur ses no-op.</summary>
    public static CalibrationRapport Juger(
        IReadOnlyList<NoOpObservation> observations, GameMetricKind metrique)
    {
        var v = (observations ?? Array.Empty<NoOpObservation>())
            .Where(o => o.Metrique == metrique
                        && double.IsFinite(o.DeltaPct) && double.IsFinite(o.ResolutionPct)
                        // Relue sans resolution, une ligne vaut 0 et chaque ecart deviendrait un depassement.
                        && o.ResolutionPct > 0
                        // Une observation prise hors focus ne dit rien de la machine en jeu.
                        && o.FocusMinimal is null or >= FocusObservation.FractionMinimale)
            .ToList();

        var n = v.Count;
        var depassements = v.Count(o => o.Depassement);
        var taux = n > 0 ? 100.0 * depassements / n : 0.0;

        var ecarts = v.Select(o => Math.Abs(o.DeltaPct)).OrderBy(x => x).ToList();
        var resolutions = v.Select(o => o.ResolutionPct).OrderBy(x => x).ToList();
        var ecartP95 = Percentile(ecarts, 0.95);
        var resolutionMediane = Percentile(resolutions, 0.50);

        var verdict = n < ObservationsMinimum
            ? CalibrationVerdict.PasAssezDObservations
            // Test exact et pas approximation normale : a p = 0,05, l'approximation normale
            // demande n >= 100 pour valoir quelque chose, et nous n'aurons jamais cent sessions
            // de controle avant d'avoir besoin du verdict.
            : ProbaAuMoins(depassements, n, TauxAttendu) < 0.05
                ? CalibrationVerdict.Surconfiant
                : double.IsFinite(ecartP95) && double.IsFinite(resolutionMediane)
                  && resolutionMediane > 0 && ecartP95 < resolutionMediane / 2.0
                    ? CalibrationVerdict.SousConfiant
                    : CalibrationVerdict.Calibre;

        return new CalibrationRapport(
            metrique, n, depassements, taux, ecartP95, resolutionMediane, verdict);
    }

    /// <summary>P(X ≥ k) pour X ~ Binomiale(n, p). La question exacte : « en observant k
    /// depassements sur n controles, quelle chance avait un modele honnete de faire au moins aussi
    /// mal ? » Une reponse sous 5 % accuse le modele, pas le hasard.</summary>
    private static double ProbaAuMoins(int k, int n, double p)
    {
        if (k <= 0) return 1.0;
        if (k > n) return 0.0;

        // Somme des termes k..n, calculee par recurrence sur le terme precedent pour eviter des
        // factorielles qui deborderaient bien avant d'etre utiles.
        var terme = Math.Pow(1 - p, n);   // i = 0
        var somme = 0.0;
        for (var i = 0; i <= n; i++)
        {
            if (i >= k) somme += terme;
            if (i < n) terme *= p / (1 - p) * (n - i) / (double)(i + 1);
        }
        return Math.Min(1.0, somme);
    }

    private static double Percentile(List<double> trie, double fraction)
    {
        if (trie.Count == 0) return double.NaN;
        var idx = (int)Math.Ceiling(fraction * trie.Count) - 1;
        return trie[Math.Clamp(idx, 0, trie.Count - 1)];
    }
}
