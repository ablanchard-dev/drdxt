using System.Windows;

namespace DrDxt.App.ViewModels;

/// <summary>
/// La courbe des temps de trame de CETTE mesure, en pixels, et sa médiane. Ce n'est jamais un avant/après : l'App n'a qu'une
/// passe. Chaque colonne garde la trame la plus lente qu'elle couvre, pour que les à-coups se voient au-dessus de la médiane
/// au lieu de se fondre dans une moyenne. Aucune bande de bruit : la marge de la mesure est l'incertitude de la MOYENNE des
/// FPS entre passes, pas la dispersion des trames ; tracée sous des maxima, elle faisait voir des à-coups partout.
/// </summary>
/// <param name="Points">La courbe, de gauche à droite ; y croît vers le bas (coordonnées WPF).</param>
/// <param name="PicsEcretes">Les points au-dessus de l'échelle, dessinés écrêtés en haut : marqués et comptés, jamais
/// cachés sans être dits.</param>
public sealed record CourbeDesTempsDeTrame(IReadOnlyList<Point> Points, double MedianeY, double MaxMs, IReadOnlyList<Point> PicsEcretes)
{
    public const double Largeur = 600, Hauteur = 120;
    public const int Colonnes = 300;

    public static CourbeDesTempsDeTrame? Construire(IReadOnlyList<double>? imagesMs, int colonnes = Colonnes,
        double largeur = Largeur, double hauteur = Hauteur)
    {
        if (imagesMs is not { Count: > 0 }) return null;
        var n = Math.Min(colonnes, imagesMs.Count);
        var lentes = Enumerable.Range(0, n)
            .Select(i => Enumerable.Range(i * imagesMs.Count / n, (i + 1) * imagesMs.Count / n - i * imagesMs.Count / n).Max(k => imagesMs[k]))
            .ToList();
        var triees = imagesMs.Order().ToList();
        var mediane = triees.Count % 2 == 1 ? triees[triees.Count / 2] : (triees[triees.Count / 2 - 1] + triees[triees.Count / 2]) / 2;
        // L'axe va au 99e centile des points tracés (rang le plus proche), avec un peu d'air : un pic isolé écrasait sinon toute
        // la courbe. Ce qui dépasse est écrêté en haut et compté.
        var p99 = lentes.Order().ElementAt((int)Math.Ceiling(0.99 * n) - 1);
        var echelle = p99 * 1.1;
        double Y(double ms) => Math.Max(0, hauteur - ms / echelle * hauteur);
        double X(int i) => n == 1 ? 0 : i * largeur / (n - 1);
        var points = lentes.Select((ms, i) => new Point(X(i), Y(ms))).ToList();
        var pics = lentes.Select((ms, i) => (ms, i)).Where(x => x.ms > echelle).Select(x => new Point(X(x.i), 0)).ToList();
        return new(points, Y(mediane), lentes.Max(), pics);
    }
}
