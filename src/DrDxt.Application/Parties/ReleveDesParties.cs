using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Une capture courte prise pendant une partie : ses FPS, son 1 % low, et le goulot que le classifieur
/// en a tiré. 04/10/2026 : la fréquence moyenne des cœurs rapides et les watts du paquet à la fin de la capture,
/// <c>null</c> quand le compteur manque (machine sans eux, ou relevé raté).</summary>
public sealed record CaptureEnPartie(DateTimeOffset Quand, double Fps, double Low1, BottleneckState Goulot,
    double? Mhz = null, double? Watts = null, double? Celsius = null, double? VramPourcent = null,
    IReadOnlyList<(string Nom, double Pourcent)>? Voisins = null, double? CarteCelsius = null,
    IReadOnlyList<string>? Overlays = null, double? PingBox = null, double? PingInternet = null, double? PertesInternet = null,
    string? CarteDuJeu = null, string? CarteDedieeIgnoree = null, MecanismeCarte? MecanismeCarte = null,
    // B4 (10/10/2026) : la latence d'affichage médiane de la capture (PresentMon), en ms ; null = non livrée.
    double? LatenceAffichageMs = null);

/// <summary>04/10/2026 : un programme qui a chargé le processeur à côté du jeu : son pic (en % d'un cœur, tous ses
/// processus additionnés) et le nombre de relevés où il dépassait le seuil.</summary>
public sealed record VoisinEnPartie(string Nom, double PourcentMax, int Captures);

/// <summary>Une partie jouée, résumée : de la première à la dernière capture, les médianes, le goulot le plus vu.</summary>
public sealed record PartieRelevee(
    string Jeu, DateTimeOffset Debut, DateTimeOffset Fin, int Captures, double FpsMedian, double Low1Median, BottleneckState Goulot,
    double? MhzMedian = null, double? WattsMedian = null, double? CelsiusMax = null, double? BancMhz = null, double? VramPourcentMax = null,
    IReadOnlyList<VoisinEnPartie>? Voisins = null, double? CarteCelsiusMax = null, string? Essai = null,
    IReadOnlyList<string>? Overlays = null, double? PingBoxMedian = null, double? PingInternetMedian = null, double? PertesInternetMax = null,
    string? CarteDuJeu = null, string? CarteDedieeIgnoree = null, MecanismeCarte? MecanismeCarte = null,
    // B4 (10/10/2026) : la latence d'affichage médiane de la partie, en ms ; null quand aucune capture ne l'a.
    double? LatenceAffichageMedianMs = null);

/// <summary>
/// 29/09/2026 (boucle « vivant et prouvé », A3) : aucune partie jouée ne laissait de trace dans DrDXT. La veille
/// prend des captures courtes pendant la partie ; à la fin, une ligne. Les médianes résistent à une capture prise
/// pendant un chargement ou un écran de mort. « Inconnu » ne gagne jamais au nombre contre un goulot mesuré.
/// </summary>
public static class ReleveDesParties
{
    /// <summary>Assez pour voir une tendance d'un soir à l'autre, sans grossir sans fin.</summary>
    public const int PartiesGardees = 20;

    public const int CapturesMinimum = 2;

    public static PartieRelevee? Resumer(string jeu, IReadOnlyList<CaptureEnPartie> captures)
    {
        // 06/10/2026 (revue F-5) : une seule capture de 20 s n'est pas une partie (une fenêtre plein écran de passage).
        if (captures.Count < CapturesMinimum) return null;
        var mesures = captures.Where(c => c.Goulot != BottleneckState.UnknownPartial).ToList();
        var goulot = mesures.Count == 0
            ? BottleneckState.UnknownPartial
            : mesures.GroupBy(c => c.Goulot).OrderByDescending(g => g.Count()).First().Key;
        return new PartieRelevee(jeu, captures.Min(c => c.Quand), captures.Max(c => c.Quand), captures.Count,
            Mediane(captures.Select(c => c.Fps)), Mediane(captures.Select(c => c.Low1)), goulot,
            MedianeLue(captures.Select(c => c.Mhz)), MedianeLue(captures.Select(c => c.Watts)),
            // La chaleur se juge à son pic, pas à sa médiane.
            captures.Select(c => c.Celsius).OfType<double>().DefaultIfEmpty().Max() is var t and > 0 ? t : null)
        {
            // 04/10/2026 : la mémoire vidéo aussi, à son pic : une saturation fait des à-coups même brève.
            VramPourcentMax = captures.Select(c => c.VramPourcent).OfType<double>().DefaultIfEmpty().Max() is var v and > 0 ? v : null,
            // B4 (10/10/2026) : la latence d'affichage, à sa médiane, comme les images.
            LatenceAffichageMedianMs = MedianeLue(captures.Select(c => c.LatenceAffichageMs)),
            Voisins = VoisinsLourds(captures),
            // 07/10/2026 (A5) : la carte aussi, à son pic ; non lue sur toutes les captures, elle reste non lue.
            CarteCelsiusMax = captures.Select(c => c.CarteCelsius).OfType<double>().DefaultIfEmpty().Max() is var g and > 0 ? g : null,
            // 07/10/2026 (D10) : tout overlay vu sur une capture ; aucun relevé lu, rien d'affirmé.
            Overlays = captures.All(c => c.Overlays is null) ? null
                : captures.SelectMany(c => c.Overlays ?? []).Distinct().Order(StringComparer.Ordinal).ToList(),
            // 07/10/2026 (D6, enquête n° 14) : le ping pendant la partie, à sa médiane ; les pertes à leur pic.
            PingBoxMedian = MedianeLue(captures.Select(c => c.PingBox)),
            PingInternetMedian = MedianeLue(captures.Select(c => c.PingInternet)),
            PertesInternetMax = captures.Select(c => c.PertesInternet).OfType<double>().DefaultIfEmpty(-1).Max() is var pe and >= 0 ? pe : null,
            // 07/10/2026 (A4) : la carte qui a le plus porté le jeu ; et la vraie carte laissée de côté, dès qu'une capture
            // a vu le jeu sur une puce intégrée alors qu'elle était là.
            CarteDuJeu = captures.Select(c => c.CarteDuJeu).OfType<string>().GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            CarteDedieeIgnoree = captures.Select(c => c.CarteDedieeIgnoree).OfType<string>().FirstOrDefault(),
            // 08/10/2026 (J-MECA) : ce que la limite de puissance a fait pendant la partie, par le pilote.
            MecanismeCarte = MecanismeCarte.Cumuler(captures.Select(c => c.MecanismeCarte)),
        };
    }

    /// <summary>07/10/2026 (D6) : la route qui se dégrade d'une partie à l'autre. Même règle que les autres juges :
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

    /// <summary>04/10/2026 : même seuil que le calmeur de voisins, un quart de cœur tenu.</summary>
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

    /// <summary>04/10/2026 : au-dessus, la mémoire vidéo est pleine et le jeu déborde en mémoire système. **NON CHIFFRÉ** :
    /// Windows garde une part de la carte pour lui-même, donc le budget d'un jeu est sous la taille physique ; 95 % de la
    /// carte entière n'arrive qu'au moment où ce budget est déjà dépassé.</summary>
    public const double SeuilVramPleine = 95;

    public static bool VramPleine(PartieRelevee p) => p.VramPourcentMax >= SeuilVramPleine;

    /// <summary>04/10/2026 (plan 1.1) : sous ce rapport de la fréquence tenue par le banc de 45 s, le processeur ralentit
    /// en partie alors qu'il tient en charge courte. **NON CHIFFRÉ** : 90 % laisse passer l'écart normal entre une charge
    /// de jeu et la charge du banc (5 100 contre 5 190 MHz relevés sur la machine de référence) et attrape la marche de
    /// 5,3 à 3,7 GHz vue en partie le 09/09.</summary>
    public const double SeuilRalentissement = 0.9;

    /// <summary>Vrai quand la fréquence médiane en partie tombe sous <see cref="SeuilRalentissement"/> de celle que le
    /// banc tenait À L'ÉPOQUE de la partie (<see cref="PartieRelevee.BancMhz"/>, gardé à l'enregistrement), et seulement
    /// si la partie était limitée par le processeur. Revue du 04/10 (P1) : comparées au banc d'aujourd'hui, les
    /// parties d'avant un changement de BIOS se disaient « ralenties » ; et un jeu limité par la carte ou plafonné charge
    /// peu les cœurs, leur fréquence basse n'y est pas un ralentissement. Sans l'une des valeurs : faux.</summary>
    public static bool RalentitEnPartie(PartieRelevee p)
        => p.Goulot == BottleneckState.CpuBoundOrAppBound
           && p.MhzMedian is { } enPartie && p.BancMhz is { } banc && banc > 0
           && enPartie < SeuilRalentissement * banc;

    /// <summary>Au-dessus, le ralentissement se dit « la chaleur » ; en dessous ou sans relevé, la cause n'est pas
    /// nommée (chaleur ou limite de puissance). **NON CHIFFRÉ** : 90 °C laisse 10 °C sous le freinage des Intel récents
    /// (100 °C) et reste sous le régime normal d'un Ryzen 7000 (95 °C) ; pour ce dernier, la phrase dit seulement chaud.</summary>
    public const double SeuilChaleurC = 90;

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
