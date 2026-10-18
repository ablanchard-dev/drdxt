using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Une capture courte prise pendant une partie : ses FPS, son 1 % low, et le goulot que le classifieur
/// en a tiré. La fréquence moyenne des cœurs rapides et les watts du paquet à la fin de la capture,
/// <c>null</c> quand le compteur manque (machine sans eux, ou relevé raté).</summary>
public sealed record CaptureEnPartie(DateTimeOffset Quand, double Fps, double Low1, BottleneckState Goulot,
    double? Mhz = null, double? Watts = null, double? Celsius = null, double? VramPourcent = null,
    IReadOnlyList<(string Nom, double Pourcent)>? Voisins = null, double? CarteCelsius = null,
    IReadOnlyList<string>? Overlays = null, double? PingBox = null, double? PingInternet = null, double? PertesInternet = null,
    string? CarteDuJeu = null, string? CarteDedieeIgnoree = null, MecanismeCarte? MecanismeCarte = null,
    // La latence d'affichage médiane de la capture (PresentMon), en ms ; null = non livrée.
    double? LatenceAffichageMs = null,
    // Toutes les images de la fenêtre (temps de trame, ms) ; null = inconnues (rejeu, test, instrument sans elles).
    IReadOnlyList<double>? ImagesMs = null,
    // La part du temps où le jeu était au premier plan (0-1), et la part des secondes avec une entrée du joueur
    // (clavier, souris, manette). null = non observé : ni pour ni contre.
    double? PremierPlan = null, double? Entrees = null,
    // Les secondes du processeur (cœur le plus chaud, cœur 0, raisons de freiner) ; null = PawnIO absent.
    FreinageCpu? Freinage = null,
    // Chaque ventilateur de la carte sur la capture ; null = non lus.
    IReadOnlyList<VentilateurEnPartie>? Ventilateurs = null,
    // La part des images composées par Windows (0-1) et la latence PC médiane (ms) de la capture ; null = non lues.
    double? PartComposee = null, double? LatencePcMs = null,
    // La mémoire du jeu hors de la carte (mémoire système), en Mo ; null = non lue.
    double? HorsCarteMo = null);

/// <summary>Un programme qui a chargé le processeur à côté du jeu : son pic (en % d'un cœur, tous ses
/// processus additionnés) et le nombre de relevés où il dépassait le seuil.</summary>
public sealed record VoisinEnPartie(string Nom, double PourcentMax, int Captures);

/// <summary>Une partie jouée, résumée : de la première à la dernière capture, les médianes, le goulot le plus vu.</summary>
public sealed record PartieRelevee(
    string Jeu, DateTimeOffset Debut, DateTimeOffset Fin, int Captures, double FpsMedian, double Low1Median, BottleneckState Goulot,
    double? MhzMedian = null, double? WattsMedian = null, double? CelsiusMax = null, double? BancMhz = null, double? VramPourcentMax = null,
    IReadOnlyList<VoisinEnPartie>? Voisins = null, double? CarteCelsiusMax = null, string? Essai = null,
    IReadOnlyList<string>? Overlays = null, double? PingBoxMedian = null, double? PingInternetMedian = null, double? PertesInternetMax = null,
    string? CarteDuJeu = null, string? CarteDedieeIgnoree = null, MecanismeCarte? MecanismeCarte = null,
    // La latence d'affichage médiane de la partie, en ms ; null quand aucune capture ne l'a.
    double? LatenceAffichageMedianMs = null,
    // Les minutes vraiment jouées (menus, lobby, chargements et autres fenêtres mis à part) ; null = la veille
    // n'avait pas les images, rien n'est affirmé.
    double? MinutesJouees = null,
    // Le freinage du processeur sur les fenêtres jouées ; null = non lu.
    FreinageCpu? Freinage = null,
    // Chaque ventilateur de la carte sur toute la partie ; null = non lus.
    IReadOnlyList<VentilateurEnPartie>? Ventilateurs = null,
    // La part des images composées par Windows (0-1) et la latence PC (ms), à leur médiane ; null = non lues.
    double? PartComposeeMediane = null, double? LatencePcMedianMs = null,
    // La mémoire du jeu hors de la carte, à son pic, en Mo ; null = non lue.
    double? HorsCarteMoMax = null,
    // Vrai quand le 1 % low porte sur toutes les images des fenêtres jouées ; faux quand c'est la médiane des 1 % low de
    // chaque fenêtre (une fenêtre sans ses images, ou une partie écrite avant). Deux estimateurs ne se comparent pas.
    bool Low1SurToutesLesImages = false);

/// <summary>
/// Aucune partie jouée ne laissait de trace dans DrDXT. La veille
/// prend des captures pendant la partie ; à la fin, une ligne. « Inconnu » ne gagne jamais au nombre contre un goulot mesuré.
///
/// Les captures
/// se suivent sans trou et portent toutes leurs images ; les FPS et le 1 % low se calculent sur toutes les images des
/// fenêtres jouées, pas sur une médiane de fenêtres. Les champs gardent leur nom (<c>FpsMedian</c>, <c>Low1Median</c>)
/// pour ne pas casser les parties déjà écrites : sans images (rejeu, test), ils restent des médianes de captures.
///
/// Une fenêtre n'est pas jouée (« quand FPS bloqué c'est menu, ou navigation vers une autre fenêtre ») quand le jeu
/// n'était pas au premier plan, quand le joueur n'a rien touché, ou quand les images sont bloquées à un plafond
/// nettement sous celui de la partie (Call of Duty plafonne les menus plus bas que le match : 100 contre 155 sur la
/// machine de référence). Un plafond au niveau de la partie (cap en jeu, G-Sync) reste joué.
/// </summary>
public static class ReleveDesParties
{
    /// <summary>Assez pour voir une tendance d'un soir à l'autre, sans grossir sans fin.</summary>
    public const int PartiesGardees = 20;

    public const int CapturesMinimum = 2;

    /// <summary>Sous ce nombre d'images, une capture n'a pas « ses images » : ses chiffres agrégés font foi.</summary>
    public const int ImagesMinimum = 200;
    /// <summary>Jeu au premier plan moins de la moitié du temps : le joueur était ailleurs.</summary>
    public const double PremierPlanMinimum = 0.5;
    /// <summary>Moins de trois secondes sur dix avec une entrée : on attend, on ne joue pas.</summary>
    public const double EntreesMinimum = 0.3;
    /// <summary>Temps de trame quasi constants (écart-type / moyenne) : des images bloquées à un plafond.</summary>
    public const double CvPlafond = 0.03;
    /// <summary>Un plafond sous 85 % de celui de la partie est celui d'un menu, pas du jeu.</summary>
    public const double PartDuPlafondMenu = 0.85;

    /// <summary>Pur : la fenêtre a-t-elle été jouée ? <paramref name="fpsDeReference"/> = le niveau des fenêtres où le
    /// joueur jouait (premier plan + entrées), pour reconnaître un plafond de menu.</summary>
    public static bool EstJouee(CaptureEnPartie c, double fpsDeReference)
    {
        if (c.PremierPlan is { } plan && plan < PremierPlanMinimum) return false;
        if (c.Entrees is { } entrees && entrees < EntreesMinimum) return false;
        if (c.ImagesMs is { Count: >= ImagesMinimum } images && Cv(images) < CvPlafond && c.Fps < PartDuPlafondMenu * fpsDeReference)
            return false;
        return true;
    }

    /// <summary>Le niveau de FPS des fenêtres où le joueur jouait (premier plan + entrées) ; toutes à défaut.</summary>
    public static double FpsDeReference(IReadOnlyList<CaptureEnPartie> captures)
    {
        var actives = captures.Where(c => c.PremierPlan is not < PremierPlanMinimum && c.Entrees is not < EntreesMinimum).ToList();
        return Mediane((actives.Count > 0 ? actives : captures).Select(c => c.Fps));
    }

    private static double Cv(IReadOnlyList<double> images)
    {
        var moyenne = images.Average();
        if (moyenne <= 0) return double.PositiveInfinity;
        return Math.Sqrt(images.Sum(x => (x - moyenne) * (x - moyenne)) / (images.Count - 1)) / moyenne;
    }

    public static PartieRelevee? Resumer(string jeu, IReadOnlyList<CaptureEnPartie> captures)
    {
        // Une seule capture n'est pas une partie (une fenêtre plein écran de passage).
        if (captures.Count < CapturesMinimum) return null;
        var reference = FpsDeReference(captures);
        var vraimentJouees = captures.Where(c => EstJouee(c, reference)).ToList();
        // Une partie passée entière au lobby reste une partie relevée : ses chiffres, avec zéro minute jouée.
        var jouees = vraimentJouees.Count > 0 ? vraimentJouees : captures;
        var mesures = jouees.Where(c => c.Goulot != BottleneckState.UnknownPartial).ToList();
        var goulot = mesures.Count == 0
            ? BottleneckState.UnknownPartial
            : mesures.GroupBy(c => c.Goulot).OrderByDescending(g => g.Count()).First().Key;
        // Toutes les images des fenêtres jouées ; une seule fenêtre sans ses images, et tout retombe sur les médianes.
        var images = jouees.All(c => c.ImagesMs is { Count: >= ImagesMinimum }) ? jouees.SelectMany(c => c.ImagesMs!).ToList() : null;
        double? minutesJouees = captures.Any(c => c.ImagesMs is { Count: >= ImagesMinimum })
            ? vraimentJouees.Sum(c => (c.ImagesMs ?? []).Sum()) / 60_000 : null;
        return new PartieRelevee(jeu, captures.Min(c => c.Quand), captures.Max(c => c.Quand), captures.Count,
            images is null ? Mediane(jouees.Select(c => c.Fps)) : DrDxt.Measurement.FrametimeStatistics.AvgFps(images),
            images is null ? Mediane(jouees.Select(c => c.Low1)) : DrDxt.Measurement.FrametimeStatistics.P1LowFps(images), goulot,
            MedianeLue(jouees.Select(c => c.Mhz)), MedianeLue(jouees.Select(c => c.Watts)),
            // La chaleur se juge à son pic, pas à sa médiane.
            captures.Select(c => c.Celsius).OfType<double>().DefaultIfEmpty().Max() is var t and > 0 ? t : null)
        {
            MinutesJouees = minutesJouees,
            Low1SurToutesLesImages = images is not null,
            Freinage = FreinageCpu.Cumuler(jouees.Select(c => c.Freinage)),
            // Sur toutes les captures : un ventilateur qui s'arrête dans un menu s'arrête quand même.
            Ventilateurs = VentilateursEnPartie.Cumuler(captures.Select(c => c.Ventilateurs)),
            // La mémoire vidéo aussi, à son pic : une saturation fait des à-coups même brève.
            VramPourcentMax = captures.Select(c => c.VramPourcent).OfType<double>().DefaultIfEmpty().Max() is var v and > 0 ? v : null,
            // La latence d'affichage, à sa médiane, sur les fenêtres jouées comme les images : le menu n'est pas la partie.
            LatenceAffichageMedianMs = MedianeLue(jouees.Select(c => c.LatenceAffichageMs)),
            PartComposeeMediane = MedianeLue(jouees.Select(c => c.PartComposee)),
            LatencePcMedianMs = MedianeLue(jouees.Select(c => c.LatencePcMs)),
            // À son pic, comme la mémoire vidéo : un débordement fait des à-coups même bref.
            HorsCarteMoMax = captures.Select(c => c.HorsCarteMo).OfType<double>().DefaultIfEmpty(-1).Max() is var hc and >= 0 ? hc : null,
            Voisins = VoisinsLourds(captures),
            // La carte aussi, à son pic ; non lue sur toutes les captures, elle reste non lue.
            CarteCelsiusMax = captures.Select(c => c.CarteCelsius).OfType<double>().DefaultIfEmpty().Max() is var g and > 0 ? g : null,
            // Les overlays de toutes les captures réunis ; si aucune capture ne les a lus, rien d'affirmé.
            Overlays = captures.All(c => c.Overlays is null) ? null
                : captures.SelectMany(c => c.Overlays ?? []).Distinct().Order(StringComparer.Ordinal).ToList(),
            // Le ping pendant la partie, à sa médiane ; les pertes à leur pic.
            PingBoxMedian = MedianeLue(captures.Select(c => c.PingBox)),
            PingInternetMedian = MedianeLue(captures.Select(c => c.PingInternet)),
            PertesInternetMax = captures.Select(c => c.PertesInternet).OfType<double>().DefaultIfEmpty(-1).Max() is var pe and >= 0 ? pe : null,
            // La carte qui a le plus porté le jeu ; et la vraie carte laissée de côté, dès qu'une capture
            // a vu le jeu sur une puce intégrée alors qu'elle était là.
            CarteDuJeu = captures.Select(c => c.CarteDuJeu).OfType<string>().GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            CarteDedieeIgnoree = captures.Select(c => c.CarteDedieeIgnoree).OfType<string>().FirstOrDefault(),
            // Ce que la limite de puissance a fait pendant la partie, par le pilote.
            MecanismeCarte = MecanismeCarte.Cumuler(captures.Select(c => c.MecanismeCarte)),
        };
    }

    /// <summary>La route qui se dégrade d'une partie à l'autre. Même règle que les autres juges :
    /// séparation nette ou rien (les <see cref="PartiesRoute"/> dernières parties toutes au-dessus des
    /// <see cref="PartiesRoute"/> d'avant). La box dit où : si son ping monte aussi, c'est le lien jusqu'à elle (Wi-Fi,
    /// câble, box) ; sinon c'est au-delà, chez le fournisseur d'accès ou plus loin.</summary>
    public sealed record RouteDegradee(double Avant, double Apres, bool BoxAussi);

    public const int PartiesRoute = 3;

    public static RouteDegradee? RouteQuiSeDegrade(IReadOnlyList<PartieRelevee> parties)
    {
        var lues = parties.Where(p => p.PingInternetMedian is not null).OrderBy(p => p.Debut).ToList();
        if (lues.Count < 2 * PartiesRoute) return null;
        var apres = lues.TakeLast(PartiesRoute).ToList();
        var avant = lues.SkipLast(PartiesRoute).TakeLast(PartiesRoute).ToList();
        if (apres.Min(p => p.PingInternetMedian!.Value) <= avant.Max(p => p.PingInternetMedian!.Value)) return null;
        var boxAussi = apres.All(p => p.PingBoxMedian is not null) && avant.All(p => p.PingBoxMedian is not null)
            && apres.Min(p => p.PingBoxMedian!.Value) > avant.Max(p => p.PingBoxMedian!.Value);
        double M(IEnumerable<double> v) { var t = v.Order().ToArray(); return t.Length % 2 == 1 ? t[t.Length / 2] : (t[t.Length / 2 - 1] + t[t.Length / 2]) / 2; }
        return new RouteDegradee(M(avant.Select(p => p.PingInternetMedian!.Value)), M(apres.Select(p => p.PingInternetMedian!.Value)), boxAussi);
    }

    /// <summary>Même seuil que le calmeur de voisins, un quart de cœur tenu.</summary>
    public const double SeuilVoisinPourcent = 25;

    /// <summary>Par relevé, les processus d'un même nom s'additionnent (un navigateur en a des dizaines) ; un voisin
    /// gardé dépasse le seuil sur au moins deux relevés (un seul = un pic qui passe). <c>null</c> si rien n'a été lu.</summary>
    private static IReadOnlyList<VoisinEnPartie>? VoisinsLourds(IReadOnlyList<CaptureEnPartie> captures)
    {
        if (captures.All(c => c.Voisins is null)) return null;
        return captures.Where(c => c.Voisins is not null)
            .SelectMany(c => c.Voisins!.GroupBy(v => v.Nom, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Nom: g.Key, Pourcent: g.Sum(v => v.Pourcent))))
            .Where(v => v.Pourcent >= SeuilVoisinPourcent)
            .GroupBy(v => v.Nom, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => new VoisinEnPartie(g.Key, g.Max(v => v.Pourcent), g.Count()))
            .OrderByDescending(v => v.PourcentMax)
            .ToList();
    }

    /// <summary>Au-dessus, la mémoire vidéo est pleine et le jeu déborde en mémoire système. **Non chiffré** :
    /// Windows garde une part de la carte pour lui-même, donc le budget d'un jeu est sous la taille physique ; 95 % de la
    /// carte entière n'arrive qu'au moment où ce budget est déjà dépassé.</summary>
    public const double SeuilVramPleine = 95;

    public static bool VramPleine(PartieRelevee p) => p.VramPourcentMax >= SeuilVramPleine;

    /// <summary>Sous ce rapport de la fréquence tenue par le banc de 45 s, le processeur ralentit
    /// en partie alors qu'il tient en charge courte. **Non chiffré** : 90 % laisse passer l'écart normal entre une charge
    /// de jeu et la charge du banc (5 100 contre 5 190 MHz relevés sur la machine de référence) et attrape une marche de
    /// 5,3 à 3,7 GHz relevée en partie le 09/09.</summary>
    public const double SeuilRalentissement = 0.9;

    /// <summary>Vrai quand la fréquence médiane en partie tombe sous <see cref="SeuilRalentissement"/> de celle que le
    /// banc tenait à l'époque de la partie (<see cref="PartieRelevee.BancMhz"/>, gardé à l'enregistrement), et seulement
    /// si la partie était limitée par le processeur. Comparées au banc d'aujourd'hui, les
    /// parties d'avant un changement de BIOS passeraient pour « ralenties » ; et un jeu limité par la carte ou plafonné charge
    /// peu les cœurs, leur fréquence basse n'y est pas un ralentissement. Sans l'une des valeurs : faux.</summary>
    public static bool RalentitEnPartie(PartieRelevee p)
        => p.Goulot == BottleneckState.CpuBoundOrAppBound
           && p.MhzMedian is { } enPartie && p.BancMhz is { } banc && banc > 0
           && enPartie < SeuilRalentissement * banc;

    /// <summary>Au-dessus, le ralentissement se dit « la chaleur » ; en dessous ou sans relevé, la cause n'est pas
    /// nommée (chaleur ou limite de puissance). **Non chiffré** : 90 °C laisse 10 °C sous le freinage des Intel récents
    /// (100 °C) et reste sous le régime normal d'un Ryzen 7000 (95 °C) ; pour ce dernier, la phrase dit seulement chaud.</summary>
    public const double SeuilChaleurC = 90;

    /// <summary>
    /// Quand la partie porte au moins une minute de secondes, la cause est celle du docteur (<see cref="FreinageCpu.Raison"/> :
    /// une seule règle, PROCHOT compris) ; sinon, l'ancienne règle de la température. Rend la clé de
    /// texte et la part en %.</summary>
    public static (string Cle, double? Pourcent) CauseDuRalentissement(PartieRelevee p)
    {
        if (p.Freinage is { Secondes: >= 60 } f)
            return f.Raison() switch
            {
                RaisonDuFreinage.Courant => ("Parties.RalentitCourant", Math.Round(100 * f.Part(f.LimiteCourant))),
                RaisonDuFreinage.Chaleur => ("Parties.RalentitChaleurLue", Math.Round(100 * f.Part(f.Chaleur))),
                RaisonDuFreinage.Prochot => ("Parties.RalentitProchot", Math.Round(100 * f.Part(f.Prochot))),
                RaisonDuFreinage.Puissance => ("Parties.RalentitPuissance", Math.Round(100 * f.Part(f.LimiteW))),
                _ => ("Parties.Ralentit", null),
            };
        return (p.CelsiusMax >= SeuilChaleurC ? "Parties.RalentitChaleur" : "Parties.Ralentit", null);
    }

    private static double? MedianeLue(IEnumerable<double?> valeurs)
    {
        var lues = valeurs.OfType<double>().ToList();
        return lues.Count == 0 ? null : Mediane(lues);
    }

    /// <summary>La plus récente d'abord ; au-delà de <see cref="PartiesGardees"/>, les plus anciennes sortent.</summary>
    public static IReadOnlyList<PartieRelevee> Ajouter(IEnumerable<PartieRelevee> existantes, PartieRelevee nouvelle)
        => new[] { nouvelle }.Concat(existantes).Take(PartiesGardees).ToList();

    private static double Mediane(IEnumerable<double> valeurs)
    {
        var v = valeurs.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
