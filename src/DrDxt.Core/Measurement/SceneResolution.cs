namespace DrDxt.Core;

/// <summary>Ce qu'une scene permet de conclure, avant meme d'avoir touche a un reglage.</summary>
public enum SceneVerdict
{
    /// <summary>La scene bruite mais ne derive pas : un ecart au-dela de sa resolution est
    /// attribuable au changement.</summary>
    Usable,

    /// <summary>La scene DERIVE plus qu'elle ne bruite. Le temps qui passe y produit un ecart
    /// plus grand que le hasard, donc un A/B simple attribuerait au reglage ce qui n'est que
    /// l'horloge. Il faut un protocole apparie (a-b-a-b) ou une autre scene.</summary>
    DriftDominant,

    /// <summary>Moins de trois passes exploitables : ni dispersion, ni pente. Rien a dire.</summary>
    TooFewRuns,
}

/// <summary>
/// L'A/a : ce que cette scene sait voir, mesure sur place et avant tout levier.
///
/// DrDXT testait des leviers sans savoir ce que la scene pouvait detecter. Un +1 % teste vingt
/// minutes sur une scene qui resout +/-5 %, ce sont vingt minutes perdues d'avance — et le
/// « pas de gain » qui en sort est faux : il n'y avait pas de gain **detectable**, ce qui n'est pas
/// la meme chose. Le modele de preuve sait deja dire `NoResolvedChange` apres coup ; l'A/A permet
/// de le savoir avant de depenser le temps du joueur.
///
/// La resolution n'est pas une constante du produit : elle depend du jeu, de la scene, de la
/// machine, de la temperature, de la duree, de la phase de partie et de la charge de fond.
///
/// ══ deux choses que la moyenne confond ══
/// **Le bruit** disperse autour d'une valeur stable ; **la derive** deplace la valeur elle-meme.
/// Mesure du 01/09/2026, machine inchangee, aucun reglage touche : **136 → 148 FPS en dix minutes**.
/// Une moyenne ne distingue pas les deux, et « on ne moyenne pas une derive ». Une scene qui derive
/// plus qu'elle ne bruite transformera le TEMPS en effet du tweak, quel que soit le soin de la
/// mesure — d'ou <see cref="SceneVerdict.DriftDominant"/>, qui envoie vers un protocole apparie.
///
/// Pur, aucune I/O : l'appelant fournit les moyennes par passe, cette classe juge.
/// </summary>
/// <param name="ResolutionPct">Le plus petit ecart que cette scene peut attribuer a autre chose
/// qu'au hasard, en pourcent. <c>+∞</c> quand elle ne sait rien resoudre.</param>
/// <param name="DriftPctPerRun">La pente observee, en pourcent de la moyenne par PASSE. Signee :
/// une scene qui chauffe descend, une scene qui se remplit monte.</param>
/// <param name="Runs">Le nombre de passes reellement exploitables.</param>
public sealed record SceneResolution(
    double ResolutionPct,
    double DriftPctPerRun,
    int Runs,
    SceneVerdict Verdict)
{
    // 26/09/2026 : cette copie s'arretait a 9 degres de liberte (1,96 ensuite, marge jusqu'a 13,7 %
    // trop etroite) et rendait 1,96 sans degre de liberte. Une seule table desormais (Student).
    private static double TValue95(int ddl) => Student.T95(ddl);

    /// <summary>Un ecart de cette taille serait-il attribuable au changement dans cette scene ?
    ///
    /// Repond NON tant que la scene derive : meme un gros ecart y serait indiscernable du temps
    /// ecoule. C'est la question a poser avant de lancer une mesure de vingt minutes.</summary>
    public bool PeutDetecter(double gainPct) =>
        Verdict == SceneVerdict.Usable
        && double.IsFinite(ResolutionPct)
        && Math.Abs(gainPct) > ResolutionPct;

    /// <summary>Mesure la sensibilite d'une scene a partir de passes du meme etat.</summary>
    /// <param name="passesFps">Une valeur par passe, dans l'ordre CHRONOLOGIQUE — l'ordre porte la
    /// derive, le melanger la ferait disparaitre.</param>
    public static SceneResolution Probe(IReadOnlyList<double> passesFps)
    {
        // Une passe a zero n'est pas une mesure basse, c'est une mesure absente : la compter
        // fabriquerait une derive descendante spectaculaire.
        var v = (passesFps ?? Array.Empty<double>())
            .Where(x => x > 0 && double.IsFinite(x)).ToList();

        if (v.Count < 3)
            return new SceneResolution(double.PositiveInfinity, 0, v.Count, SceneVerdict.TooFewRuns);

        var moyenne = v.Average();
        if (!(moyenne > 0))
            return new SceneResolution(double.PositiveInfinity, 0, v.Count, SceneVerdict.TooFewRuns);

        // ── La derive : la pente d'une regression lineaire sur l'index de passe ──────────────
        // Les abscisses sont 0..n-1 : les passes sont supposees regulierement espacees, ce qui est
        // le cas d'un protocole. Une derive exprimee par passe se compare directement a un gain.
        var n = v.Count;
        var xMoyen = (n - 1) / 2.0;
        var sxy = 0.0;
        var sxx = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - xMoyen;
            sxy += dx * (v[i] - moyenne);
            sxx += dx * dx;
        }
        var pente = sxx > 0 ? sxy / sxx : 0.0;
        var derivePct = pente / moyenne * 100.0;

        // ── Le bruit : la dispersion RESIDUELLE, une fois la pente retiree ───────────────────
        // Sur les residus et non sur les valeurs brutes : sinon une scene qui derive proprement
        // annoncerait un bruit enorme, et le produit conclurait « trop bruyant » au lieu de
        // « elle derive » — deux maladies differentes, deux remedes differents.
        var sse = 0.0;
        for (var i = 0; i < n; i++)
        {
            var attendu = moyenne + pente * (i - xMoyen);
            sse += (v[i] - attendu) * (v[i] - attendu);
        }
        var ecartType = Math.Sqrt(sse / Math.Max(1, n - 2));
        var marge = TValue95(n - 2) * ecartType / Math.Sqrt(n);
        var resolutionPct = marge / moyenne * 100.0;

        // ── Lequel des deux domine ? ─────────────────────────────────────────────────────────
        // La derive CUMULEE sur la serie est ce qu'un A/B naif attribuerait au reglage : c'est
        // elle qu'il faut comparer a la resolution, pas la pente d'une seule passe.
        var deriveCumuleePct = Math.Abs(derivePct) * (n - 1);
        var verdict = deriveCumuleePct > resolutionPct ? SceneVerdict.DriftDominant : SceneVerdict.Usable;

        return new SceneResolution(resolutionPct, derivePct, n, verdict);
    }
}
