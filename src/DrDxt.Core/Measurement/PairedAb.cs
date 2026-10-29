namespace DrDxt.Core;

/// <summary>Une passe d'un protocole apparie : dans quel etat elle a ete prise, et ce qu'elle a
/// rendu. L'ordre de la liste est le protocole : le melanger detruit l'information qui compte.</summary>
/// <param name="EtatB">Vrai pour l'etat modifie (B), faux pour l'etat de reference (A).</param>
/// <param name="AvgFps">Ce que la passe a rendu. <c>0</c> ou non fini = passe inexploitable.</param>
public readonly record struct AbPass(bool EtatB, double AvgFps);

/// <summary>
/// A-b-a-b : separer l'effet du reglage de l'effet du temps.
///
/// Mesure du 07/09/2026 : deux des trois captures reelles derivent plus qu'elles ne bruitent (<see cref="SceneResolution"/> :
/// 22h07 derive de 4,93 % pour 1,83 % de bruit, 23h37 de 18,68 % pour 2,36 %) : un A/B en AAA→BBB y attribuerait le plus
/// souvent au reglage le temps ecoule. L'appairage successif ne suffit pas : il reduit la derive de <c>3d</c> a <c>d</c>
/// sans l'annuler (+14,6 % d'« effet » sur une derive pure de +10 fps par passe).
///
/// D'ou un ajustement aux moindres carres <c>fps ≈ a + b·t + c·estB</c> : <c>c</c> est l'effet degage de la derive
/// lineaire, quel que soit l'ordre (A-b-a-b, a-b-b-a ou plus long), et vaut exactement zero sur une derive pure. Un effet
/// sous la resolution reste <see cref="EvidenceOutcome.NoResolvedChange"/>, jamais un gain.
///
/// Pur, aucune I/O.
/// </summary>
public static class PairedAb
{
    /// <summary>Le minimum de passes exploitables : trois parametres a ajuster (constante, pente,
    /// effet) en demandent au moins quatre pour qu'il reste de quoi juger.</summary>
    public const int PassesMinimum = 4;

    /// <summary>
    /// Part maximale de la variance de l'etat expliquable par le temps (r² entre l'instant et
    /// l'indicatrice B). Au-dela, l'effet et la derive sont confondus.
    ///
    /// Une garde explicite, car AABB n'est pas singulier : sur quatre passes son determinant vaut 1 (la colinearite
    /// parfaite demanderait deux points). Les protocoles :
    ///
    ///   A-b-b-a  r² = 0,0   ← orthogonal : l'etat n'a aucune composante temporelle
    ///   A-b-a-b  r² = 0,2
    ///   A-a-b-b  r² = 0,8   ← rejete : l'etat est surtout une facon de dire « plus tard »
    ///
    /// Le seuil est a la moitie : au-dessus, l'etat explique davantage sa propre variance par le
    /// temps que par lui-meme, et separer les deux devient une illusion de precision.
    /// </summary>
    public const double CorrelationTempsEtatMax = 0.5;

    /// <summary>
    /// Combien de fois le residu du modele peut depasser le bruit annonce par la scene avant que
    /// le modele soit declare indefendable.
    ///
    /// Sans elle le modele fabrique des verdicts : sur un saut de phase (100 → 130 FPS au milieu, effet reel +1 %), la
    /// droite rendait -5,4 %, « degradation prouvee » (mesure du 07/09/2026), avec des residus a 8,3 % de la base pour 2 %
    /// de bruit annonce. **Non chiffré** : le facteur 2 laisse passer une courbure douce et arrete un saut.
    /// </summary>
    public const double ResiduToleretouteFoisLaResolution = 2.0;

    /// <summary>Sous ce seuil, la marge du modèle n'est que l'arrondi des flottants d'un résidu nul, pas un bruit mesuré.</summary>
    public const double ResolutionNumeriqueMinPct = 1e-6;

    /// <summary>La marge, quand les passes ne disent pas leur bruit : un choix de DrDXT, pas une mesure. Un écart plus petit ne
    /// se prouve pas sur des passes sans bruit visible.</summary>
    public const double PlancherSansBruitPct = 1.0;

    /// <summary>Juge une sequence appariee.</summary>
    /// <param name="passes">Les passes dans l'ordre chronologique du protocole.</param>
    /// <param name="resolutionPct">Ce que la scene sait resoudre, mesure par
    /// <see cref="SceneResolution"/> : l'appairage change le signal, pas le bruit de la scene.</param>
    public static PerformanceEvidence Analyse(IReadOnlyList<AbPass> passes, double resolutionPct)
    {
        var toutes = passes ?? Array.Empty<AbPass>();

        // L'index de temps est celui de la sequence complete, pas de la liste filtree : une
        // passe perdue laisse un trou dans le temps, et le reboucher ferait mentir la pente.
        var points = toutes
            .Select((p, t) => (T: (double)t, Z: p.EtatB ? 1.0 : 0.0, Y: p.AvgFps))
            .Where(p => p.Y > 0 && double.IsFinite(p.Y))
            .ToList();

        if (points.Count < PassesMinimum)
            return Invalide(Docteur.T("PA.Minimum", PassesMinimum, points.Count));

        var refA = points.Where(p => p.Z == 0).ToList();
        var refB = points.Where(p => p.Z == 1).ToList();
        if (refA.Count == 0 || refB.Count == 0)
            return Invalide(Docteur.T("PA.UnSeulEtat"));

        var tMoyen = points.Average(p => p.T);
        var zMoyen = points.Average(p => p.Z);
        var yMoyen = points.Average(p => p.Y);

        double stt = 0, szz = 0, stz = 0, sty = 0, szy = 0;
        foreach (var (t, z, y) in points)
        {
            var dt = t - tMoyen;
            var dz = z - zMoyen;
            var dy = y - yMoyen;
            stt += dt * dt;
            szz += dz * dz;
            stz += dt * dz;
            sty += dt * dy;
            szy += dz * dy;
        }

        var determinant = stt * szz - stz * stz;
        if (!(stt > 0) || !(szz > 0) || Math.Abs(determinant) < 1e-9)
            return Invalide(Docteur.T("PA.RienASeparer"));

        // Le protocole est-il capable de distinguer l'etat du temps ? Voir CorrelationTempsEtatMax :
        // AABB echoue ici, et c'est le seul endroit ou il pouvait etre attrape.
        var r2 = stz * stz / (stt * szz);
        if (r2 > CorrelationTempsEtatMax)
            return Invalide(Docteur.T("PA.Confondu", r2));

        // c = l'effet, en unites de FPS, une fois la derive lineaire retiree.
        var effetFps = (stt * szy - stz * sty) / determinant;

        // Rapporte a la reference : c'est un pourcentage de l'etat A, pas de la moyenne generale,
        // sinon l'effet se diluerait dans sa propre presence.
        var baseA = refA.Average(p => p.Y);
        if (!(baseA > 0))
            return Invalide(Docteur.T("PA.SansReference"));

        // ── Le modele decrit-il seulement ces donnees ? ─────────────────────────────────────
        // On reconstruit la droite ajustee et on regarde ce qu'elle laisse. Un residu bien plus
        // grand que le bruit annonce par la scene signifie que la forme de la derive n'est pas
        // celle qu'on retire, et l'effet extrait n'est alors qu'un artefact du desajustement.
        var pente = (szz * sty - stz * szy) / determinant;
        var constante = yMoyen - pente * tMoyen - effetFps * zMoyen;
        var sse = points.Sum(pt =>
        {
            var attendu = constante + pente * pt.T + effetFps * pt.Z;
            return (pt.Y - attendu) * (pt.Y - attendu);
        });
        var residuPct = Math.Sqrt(sse / Math.Max(1, points.Count - 3)) / baseA * 100.0;

        if (double.IsFinite(resolutionPct) && resolutionPct > 0
            && residuPct > ResiduToleretouteFoisLaResolution * resolutionPct)
            return Invalide(Docteur.T("PA.Modele", residuPct, resolutionPct));

        // ── La resolution du modele lui-meme ────────────────────────────────────────────────
        // L'experience mesure son propre bruit : variance du coefficient d'etat = σ²·Stt/det (σ² : le residu ci-dessus),
        // sur toutes les passes (df = n−3) et pendant l'experience. Une A/A a trois passes serait le pire estimateur
        // (t95(1) = 12,706) : sur 8 captures Warzone (07/09/2026, `tools/tailbench`), elle annoncait ±14,3 % pour un bruit
        // vrai de ±3,85 %, aveugle a tout ce qui vaut moins de 14 %.
        var varianceResiduelle = sse / Math.Max(1, points.Count - 3);
        var margeFps = TValue95(points.Count - 3) * Math.Sqrt(varianceResiduelle * stt / determinant);
        var resolutionDuModele = margeFps / baseA * 100.0;
        // Des passes exactement sur la droite (résidu nul) ne disent pas un bruit nul : elles n'en disent aucun, et une marge
        // de zéro rendrait « prouvé » le moindre écart. Le modèle cède alors à la marge de la scène, jamais sous un plancher.
        if (!(resolutionDuModele > ResolutionNumeriqueMinPct))
            resolutionDuModele = Math.Max(PlancherSansBruitPct, double.IsFinite(resolutionPct) && resolutionPct > 0 ? resolutionPct : 0);

        return new PerformanceEvidence(
            null,
            new GameEvidence("fps.moyen",
                Evidence.From(effetFps / baseA * 100.0, resolutionDuModele)));
    }

    // Une seule table dans le produit (Student). Celle-ci en etait une copie.
    private static double TValue95(int ddl) => Student.T95(ddl);

    private static PerformanceEvidence Invalide(string motif) =>
        new(null, new GameEvidence("fps.moyen", Evidence.Invalide(motif)));
}
