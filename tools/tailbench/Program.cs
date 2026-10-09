using DrDxt.Core;
using DrDxt.Measurement;

// ============================================================================================
// TENTATIVE DE DESTRUCTION DE NOTRE PROPRE CONCLUSION.
//
// La premiere version de ce banc a produit un resultat encourageant : « resolution VRAIE de
// 2,7 % sur la moyenne, 3,3 % sur le p95 frametime ». Deux choses y etaient fausses.
//
// « VRAIE » — c'est une estimation EMPIRIQUE SUR UN CORPUS de huit parties. Rien de plus.
// Le compte d'observations etait malhonnete sans que je l'aie voulu : 92 positions glissantes
//    sur 8 captures ne sont pas 92 experiences independantes. Deux positions decalees d'une
//    fenetre partagent 6 fenetres sur 7. Compter leur accord comme une confirmation revient a
//    demander deux fois son avis a la meme personne et a noter deux temoignages.
//
// Ce banc-ci repond a la seule question qui vaille : **la methode marche-t-elle sur une partie
// qu'elle n'a jamais vue ?**
//
//   --disjoint     fenetres non chevauchantes, agregation PAR CAPTURE (defaut)
//   --loco         leave-one-capture-out : choisir sur 7 captures, evaluer sur la 8e
//   --formes       controles synthetiques : un gain n'a pas toujours la forme d'un +2 % uniforme
//   --glissant     l'ancien mode, EXPLORATOIRE uniquement, jamais pour valider
//
//   dotnet run --project tools/tailbench -- [mode] [secondesParFenetre] [alternances]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var mode = args.FirstOrDefault(x => x.StartsWith("--")) ?? "--disjoint";
var nombres = args.Where(x => !x.StartsWith("--")).ToArray();
var secondes = nombres.Length > 0 && double.TryParse(nombres[0], out var s) ? s : 10.0;
var alternances = nombres.Length > 1 && int.TryParse(nombres[1], out var n) ? n : 8;

var captures = Banc.ChargerCaptures();
if (captures.Count == 0)
{
    Console.WriteLine("Aucune capture dans tools/p3. Ce banc ne simule pas de frames.");
    return 3;
}

Console.WriteLine("== BANC DE FALSIFICATION ==");
Console.WriteLine($"   {captures.Count} captures Warzone reelles distinctes — "
                  + $"{captures.Sum(c => c.Frames.Count):N0} frames, "
                  + $"{captures.Sum(c => c.Frames.Sum()) / 60000.0:0.0} minutes de jeu");
Console.WriteLine($"   fenetres de {secondes:0.#} s, {alternances} alternances "
                  + $"(protocole = {3 + alternances} fenetres, {(3 + alternances) * secondes:0} s)");
Console.WriteLine();

switch (mode)
{
    case "--glissant": Banc.Glissant(captures, secondes, alternances); break;
    case "--loco": Banc.LeaveOneCaptureOut(captures, secondes, alternances); break;
    case "--formes": Banc.Formes(captures, secondes, alternances); break;
    default: Banc.Disjoint(captures, secondes, alternances); break;
}
return 0;

/// <summary>Une partie reelle, entiere. C'est L'UNITE INDEPENDANTE de ce banc : deux experiences
/// tirees de la meme capture partagent la scene, la phase de partie, la temperature de la carte et
/// le style de jeu du moment. Elles ne temoignent pas deux fois.</summary>
internal sealed record Capture(string Nom, IReadOnlyList<double> Frames);

/// <summary>Ce qu'une experience unique a rendu.</summary>
internal readonly record struct Experience(double DeltaPct, double ResolutionPct, EvidenceOutcome Issue)
{
    public bool Depassement => Issue is EvidenceOutcome.ProvenGain or EvidenceOutcome.ProvenRegression;
    public bool Exploitable => Issue != EvidenceOutcome.Invalid && double.IsFinite(DeltaPct);
}

/// <summary>Le bilan d'UNE capture pour UNE grandeur. Agrege par capture, jamais par fenetre.</summary>
internal sealed record BilanCapture(
    string Capture,
    int Experiences,
    int Invalides,
    int FauxVerdicts,
    double EcartMaxPct,
    double ResolutionMedianeAnnoncee);

internal static class Banc
{
    /// <summary>Les grandeurs jugees, et comment chacune se lit dans le sens « plus grand =
    /// mieux ». Une seule normalisation de sens, ici, avant toute preuve.</summary>
    public static readonly (GameMetricKind Kind, string Nom, Func<IReadOnlyList<double>, double> Calc)[]
        Grandeurs =
        {
            (GameMetricKind.Moyenne, "moyenne", FrametimeStatistics.AvgFps),
            (GameMetricKind.UnPourCentLow, "1 % low", FrametimeStatistics.P1LowFps),
            (GameMetricKind.P95Frametime, "p95 ft", ft => EnFps(FrametimeStatistics.PercentileOrNull(ft, 0.95))),
        };

    /// <summary>Un frametime rendu en FPS equivalents : plus grand = mieux, comme les autres. Sans
    /// cette inversion, un p95 qui BAISSE — une frame lente raccourcie, donc un progres — porterait
    /// le signe d'une degradation.</summary>
    public static double EnFps(double? ms) => ms is > 0 ? 1000.0 / ms.Value : 0.0;

    public static IReadOnlyList<Capture> ChargerCaptures()
    {
        var dossier = Path.Combine(RacineDepot(), "tools", "p3");
        if (!Directory.Exists(dossier)) return Array.Empty<Capture>();

        var vues = new HashSet<string>();
        var captures = new List<Capture>();
        foreach (var f in Directory.GetFiles(dossier, "frames-*.txt").OrderBy(x => x))
        {
            var frames = File.ReadLines(f)
                .Select(l => double.TryParse(l, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
                .Where(v => v > 0 && double.IsFinite(v))
                .ToList();

            if (frames.Count < 3000) continue;
            // Deux fichiers du 07/09 sont des COPIES de captures du 01/09 : les compter deux fois
            // gonflerait la confiance avec le meme temoignage.
            var signature = $"{frames.Count}:{frames.Sum():F3}";
            if (vues.Add(signature))
                captures.Add(new Capture(Path.GetFileNameWithoutExtension(f)[7..], frames));
        }
        return captures;
    }

    private static string RacineDepot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>Decoupe par temps CUMULE : a nombre de frames constant, une fenetre lente durerait
    /// plus longtemps qu'une rapide et le protocole ne serait plus regulier dans le temps — or
    /// c'est le temps que le modele apparie retire.</summary>
    private static List<List<double>> Decouper(IReadOnlyList<double> frames, double secondes)
    {
        var cible = secondes * 1000.0;
        var fenetres = new List<List<double>>();
        var courante = new List<double>();
        var cumul = 0.0;
        foreach (var ft in frames)
        {
            courante.Add(ft);
            cumul += ft;
            if (cumul < cible) continue;
            fenetres.Add(courante);
            courante = new List<double>();
            cumul = 0.0;
        }
        return fenetres;
    }

    /// <summary>A-B-B-A puis son miroir B-A-A-B : chaque bloc de quatre est orthogonal au temps,
    /// donc l'assemblage l'est aussi.</summary>
    private static bool[] Etats(int nombre)
    {
        var motif = new[] { false, true, true, false };
        return Enumerable.Range(0, nombre)
            .Select(k => (k / 4) % 2 == 0 ? motif[k % 4] : !motif[k % 4]).ToArray();
    }

    /// <summary>Une experience complete : 3 fenetres d'A/A puis les alternances. La deformation
    /// <paramref name="forme"/>, quand elle existe, s'applique aux fenetres de l'etat B.</summary>
    private static Experience Experimenter(
        List<List<double>> f, int depart, int alternances,
        Func<IReadOnlyList<double>, double> estimateur,
        Func<List<double>, List<double>>? forme,
        Func<List<double>, int, int, List<double>>? derive = null)
    {
        var aa = new[] { f[depart], f[depart + 1], f[depart + 2] }.Select(w => estimateur(w)).ToList();
        var scene = SceneResolution.Probe(aa);
        var resolutionScene = scene.Verdict == SceneVerdict.Usable
            ? scene.ResolutionPct
            : double.PositiveInfinity;

        var etats = Etats(alternances);
        var passes = new List<AbPass>();
        for (var k = 0; k < alternances; k++)
        {
            var w = f[depart + 3 + k];
            // La deformation d'ETAT ne touche que B ; la deformation TEMPORELLE touche toutes les
            // passes selon leur rang. C'est la difference entre un effet et une derive, et c'est
            // exactement ce que le modele apparie doit savoir separer.
            var effective = etats[k] && forme is not null ? forme(w) : w;
            if (derive is not null) effective = derive(effective, k, alternances);
            passes.Add(new AbPass(etats[k], estimateur(effective)));
        }

        var preuve = PairedAb.Analyse(passes, resolutionScene).Game!.Preuve;
        return new Experience(preuve.DeltaPct, preuve.ResolutionPct, preuve.Outcome);
    }

    /// <summary>Les experiences DISJOINTES d'une capture : chacune consomme ses propres fenetres,
    /// aucune n'est partagee avec la suivante.</summary>
    public static List<Experience> ExperiencesDisjointes(
        Capture c, double secondes, int alternances,
        Func<IReadOnlyList<double>, double> estimateur,
        Func<List<double>, List<double>>? forme = null,
        Func<List<double>, int, int, List<double>>? derive = null)
    {
        var f = Decouper(c.Frames, secondes);
        var taille = 3 + alternances;
        var sortie = new List<Experience>();
        for (var i = 0; i + taille <= f.Count; i += taille)
            sortie.Add(Experimenter(f, i, alternances, estimateur, forme, derive));
        return sortie;
    }

    private static List<Experience> ExperiencesGlissantes(
        Capture c, double secondes, int alternances,
        Func<IReadOnlyList<double>, double> estimateur)
    {
        var f = Decouper(c.Frames, secondes);
        var taille = 3 + alternances;
        var sortie = new List<Experience>();
        for (var i = 0; i + taille <= f.Count; i++)
            sortie.Add(Experimenter(f, i, alternances, estimateur, null));
        return sortie;
    }

    private static BilanCapture Bilan(string capture, List<Experience> exp)
    {
        var bons = exp.Where(e => e.Exploitable).ToList();
        var ecarts = bons.Select(e => Math.Abs(e.DeltaPct)).OrderBy(x => x).ToList();
        var res = bons.Select(e => e.ResolutionPct).Where(double.IsFinite).OrderBy(x => x).ToList();
        return new BilanCapture(
            capture, bons.Count, exp.Count - bons.Count,
            bons.Count(e => e.Depassement),
            ecarts.Count > 0 ? ecarts[^1] : double.NaN,
            res.Count > 0 ? res[res.Count / 2] : double.NaN);
    }

    // ── MODE DISJOINT : l'evaluation honnete ────────────────────────────────────────────────
    public static void Disjoint(IReadOnlyList<Capture> captures, double secondes, int alternances)
    {
        Console.WriteLine("-- FENETRES NON CHEVAUCHANTES, AGREGE PAR CAPTURE, SOUS NO-OP --");
        Console.WriteLine("   L'effet vaut zero : tout verdict rendu ici est un FAUX verdict.");
        Console.WriteLine();

        foreach (var (kind, nom, calc) in Grandeurs)
        {
            Console.WriteLine($"   {nom}");
            Console.WriteLine($"   {"capture",-14}{"exp.",6}{"faux",6}{"|ecart| max",13}"
                              + $"{"annonce med.",14}{"invalides",11}");
            var bilans = new List<BilanCapture>();
            foreach (var c in captures)
            {
                var b = Bilan(c.Nom, ExperiencesDisjointes(c, secondes, alternances, calc));
                bilans.Add(b);
                Console.WriteLine($"   {c.Nom,-14}{b.Experiences,6}{b.FauxVerdicts,6}"
                                  + $"{b.EcartMaxPct,11:0.0} %{b.ResolutionMedianeAnnoncee,12:0.0} %"
                                  + $"{b.Invalides,11}");
            }

            Console.WriteLine($"   {"TOTAL",-14}{bilans.Sum(b => b.Experiences),6}"
                              + $"{bilans.Sum(b => b.FauxVerdicts),6}"
                              + $"      ({bilans.Count(b => b.FauxVerdicts > 0)}/{captures.Count} captures touchees)"
                              + $"   {bilans.Sum(b => b.Invalides)} invalides");
            Console.WriteLine();
        }

        Console.WriteLine("   Le nombre d'experiences DISJOINTES est le vrai budget de preuve.");
        Console.WriteLine("      Un protocole long en laisse tres peu : c'est une limite du corpus,");
        Console.WriteLine("      pas un detail de presentation.");
    }

    // ── MODE GLISSANT : exploratoire, et il faut le dire ────────────────────────────────────
    public static void Glissant(IReadOnlyList<Capture> captures, double secondes, int alternances)
    {
        Console.WriteLine("-- FENETRES GLISSANTES — EXPLORATOIRE, PAS UNE VALIDATION --");
        Console.WriteLine("   Deux positions voisines partagent presque toutes leurs frames : leur");
        Console.WriteLine("   accord ne confirme rien. A comparer au mode disjoint, jamais cite seul.");
        Console.WriteLine();
        Console.WriteLine($"   {"grandeur",-12}{"positions",11}{"faux",8}{"|ecart| p95",13}{"annonce med.",14}");

        foreach (var (kind, nom, calc) in Grandeurs)
        {
            var toutes = captures.SelectMany(c => ExperiencesGlissantes(c, secondes, alternances, calc))
                .Where(e => e.Exploitable).ToList();
            var ecarts = toutes.Select(e => Math.Abs(e.DeltaPct)).OrderBy(x => x).ToList();
            var res = toutes.Select(e => e.ResolutionPct).Where(double.IsFinite).OrderBy(x => x).ToList();
            Console.WriteLine($"   {nom,-12}{toutes.Count,11}"
                + $"{100.0 * toutes.Count(e => e.Depassement) / Math.Max(1, toutes.Count),6:0.0} %"
                + $"{(ecarts.Count > 0 ? ecarts[Math.Min(ecarts.Count - 1, (int)(0.95 * ecarts.Count))] : double.NaN),11:0.0} %"
                + $"{(res.Count > 0 ? res[res.Count / 2] : double.NaN),12:0.0} %");
        }
    }

    // ── MODE LOCO : la methode marche-t-elle sur une partie jamais vue ? ────────────────────
    public static void LeaveOneCaptureOut(IReadOnlyList<Capture> captures, double secondes, int alternances)
    {
        Console.WriteLine("-- LEAVE-ONE-CAPTURE-OUT (fenetres non chevauchantes) --");
        Console.WriteLine("   Pour chaque capture : la grandeur est CHOISIE sur les 7 autres, puis");
        Console.WriteLine("   evaluee sur celle-ci. Aucune frame du holdout ne sert au choix.");
        Console.WriteLine();
        Console.WriteLine($"   {"holdout",-14}{"choisie",10}{"exp.",6}{"faux",6}"
                          + $"{"|ecart| max",13}{"annonce",10}{"+2% vu",9}{"invalides",11}");

        int totalExp = 0, totalFaux = 0, totalVus = 0, totalEssais = 0, totalInvalides = 0;

        foreach (var holdout in captures)
        {
            var entrainement = captures.Where(c => c.Nom != holdout.Nom).ToList();

            // ── LE CHOIX, sur les 7 autres SEULEMENT ───────────────────────────────────────
            // Critere : le plus petit ecart maximal observe sous no-op, parmi les grandeurs qui
            // ne rendent aucun faux verdict. Une grandeur qui se trompe est disqualifiee avant
            // d'etre comparee — la justesse passe avant la finesse.
            var classement = Grandeurs.Select(g =>
            {
                var exp = entrainement
                    .SelectMany(c => ExperiencesDisjointes(c, secondes, alternances, g.Calc))
                    .Where(e => e.Exploitable).ToList();
                var ecarts = exp.Select(e => Math.Abs(e.DeltaPct)).OrderBy(x => x).ToList();
                return (g, Faux: exp.Count(e => e.Depassement),
                        Max: ecarts.Count > 0 ? ecarts[^1] : double.PositiveInfinity, N: exp.Count);
            }).Where(x => x.N > 0).ToList();

            if (classement.Count == 0)
            {
                Console.WriteLine($"   {holdout.Nom,-14}{"—",10}   aucune experience disjointe possible");
                continue;
            }

            var choisie = classement.OrderBy(x => x.Faux).ThenBy(x => x.Max).First().g;

            // ── L'EVALUATION, sur le holdout SEUL ──────────────────────────────────────────
            var b = Bilan(holdout.Nom, ExperiencesDisjointes(holdout, secondes, alternances, choisie.Calc));

            // Controle positif sur le meme holdout : un vrai +2 % uniforme est-il retrouve ?
            var avecEffet = ExperiencesDisjointes(holdout, secondes, alternances, choisie.Calc,
                w => w.Select(x => x / 1.02).ToList());
            var exploitables = avecEffet.Count(e => e.Exploitable);
            var vus = avecEffet.Count(e => e.Issue == EvidenceOutcome.ProvenGain);

            totalExp += b.Experiences;
            totalFaux += b.FauxVerdicts;
            totalInvalides += b.Invalides;
            totalVus += vus;
            totalEssais += exploitables;

            Console.WriteLine($"   {holdout.Nom,-14}{choisie.Nom,10}{b.Experiences,6}{b.FauxVerdicts,6}"
                              + $"{b.EcartMaxPct,11:0.0} %{b.ResolutionMedianeAnnoncee,8:0.0} %"
                              + $"{$"{vus}/{exploitables}",9}{b.Invalides,11}");
        }

        Console.WriteLine();
        Console.WriteLine($"   {captures.Count} holdouts - {totalExp} experiences independantes - "
                          + $"{totalFaux} faux verdict(s) - {totalInvalides} invalides - "
                          + $"effet +2 % retrouve {totalVus}/{totalEssais}");
    }

    // ── MODE FORMES : un gain n'a pas toujours la forme d'un +2 % uniforme ──────────────────
    public static void Formes(IReadOnlyList<Capture> captures, double secondes, int alternances)
    {
        // Le controle « toutes les frames divisees par 1,02 » ne verifie qu'UNE chose : que
        // l'estimateur retrouve un changement multiplicatif uniforme. Un vrai reglage n'a aucune
        // raison de toucher toutes les frames de la meme facon — et une politique qui ne
        // distingue pas les formes garderait un reglage qui ajoute des a-coups.
        var formes = new (string Nom, string Attendu, Func<List<double>, List<double>> F)[]
        {
            ("A  debit uniforme -2 %", "gain sur toutes les grandeurs",
                w => w.Select(x => x / 1.02).ToList()),

            ("B  queue seule amelioree", "gain sur p95 / 1 % low, moyenne ~inchangee",
                w => { var seuil = Seuil(w, 0.90); return w.Select(x => x > seuil ? x * 0.85 : x).ToList(); }),

            ("C  moyenne +3 %, a-coups ajoutes", "la queue doit se DEGRADER et interdire le keep",
                w => { var i = 0; return w.Select(x => ++i % 60 == 0 ? x * 4.0 : x * 0.97).ToList(); }),

            ("D  p95 ameliore, extreme intact", "gain p95, 1 % low ~inchange",
                w => { var bas = Seuil(w, 0.90); var haut = Seuil(w, 0.99);
                       return w.Select(x => x > bas && x <= haut ? x * 0.80 : x).ToList(); }),

        };

        // CONTROLE E, ET J'AI RATE MA PREMIERE VERSION. J'avais applique x0,70 a toutes les
        // fenetres de l'etat B et appele ca « saut de phase ». C'etait un effet UNIFORME de +43 %,
        // le banc l'a trouve et l'a declare gain — reponse correcte a la mauvaise question.
        //
        // Un saut de phase ne depend PAS de l'etat : la machine change de regime au milieu de
        // l'experience, A comme B. Le modele apparie retire une droite ; un ESCALIER ne peut pas
        // etre epouse par une droite, le residu explose, et la garde de desajustement doit
        // refuser de conclure.
        var deriveEnEscalier = new Func<List<double>, int, int, List<double>>(
            (w, rang, total) => rang >= total / 2 ? w.Select(x => x * 1.30).ToList() : w);

        var tous = formes
            .Select(x => (x.Nom, x.Attendu, F: (Func<List<double>, List<double>>?)x.F, Derive: (Func<List<double>, int, int, List<double>>?)null))
            .Append(("E  saut de phase a mi-experience",
                     "AUCUNE conclusion : le modele ne decrit pas ces donnees",
                     (Func<List<double>, List<double>>?)null, deriveEnEscalier))
            .ToArray();

        foreach (var (nomForme, attendu, f, derive) in tous)
        {
            Console.WriteLine($"   {nomForme}  →  {attendu}");
            foreach (var (kind, nom, calc) in Grandeurs)
            {
                var exp = captures
                    .SelectMany(c => ExperiencesDisjointes(c, secondes, alternances, calc, f, derive)).ToList();
                var bons = exp.Where(e => e.Exploitable).ToList();
                var median = bons.Count > 0
                    ? bons.Select(e => e.DeltaPct).OrderBy(x => x).ToList()[bons.Count / 2]
                    : double.NaN;
                Console.WriteLine($"      {nom,-10}{median,8:+0.0;-0.0} % median   "
                    + $"gain {bons.Count(e => e.Issue == EvidenceOutcome.ProvenGain),2}/{bons.Count,-2}  "
                    + $"regression {bons.Count(e => e.Issue == EvidenceOutcome.ProvenRegression),2}  "
                    + $"invalide {exp.Count - bons.Count,2}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("   La forme C est le controle qui compte : un reglage qui monte la");
        Console.WriteLine("      moyenne en ajoutant des a-coups ne doit JAMAIS ressortir gagnant");
        Console.WriteLine("      en Competitive. C'est la queue, pas la moyenne, qui doit trancher.");
    }

    private static double Seuil(List<double> w, double fraction)
    {
        var t = w.OrderBy(x => x).ToList();
        return t[Math.Clamp((int)(fraction * t.Count), 0, t.Count - 1)];
    }
}
