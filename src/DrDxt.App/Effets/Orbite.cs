using System.Windows;
using System.Windows.Media;

namespace DrDxt.App.Effets;

/// <summary>Positions sur l'orbite des pôles, partagées par le réseau et par le panneau des libellés
/// pour que chaque libellé tombe exactement sur son pôle.</summary>
public static class Orbite
{
    public static Point Position(int index, int nombre, Size taille, double centreX, double centreY,
        double rayonX, double rayonY, double angleDepartDegres)
    {
        var angle = (angleDepartDegres + 360.0 * index / Math.Max(1, nombre)) * Math.PI / 180.0;
        return new Point(
            taille.Width * centreX + Math.Cos(angle) * taille.Width * rayonX,
            taille.Height * centreY + Math.Sin(angle) * taille.Height * rayonY);
    }

    /// <summary>Une liaison : une courbe de Bézier quadratique ; un rayon va du noyau à un pôle, sinon d'un pôle à son voisin.</summary>
    public readonly record struct Liaison(Point Depart, Point Controle, Point Arrivee, bool EstRayon);

    /// <summary>Le tracé du réseau, partagé par le rendu de la boucle (tools/rendus-3d) et les éclats qui y glissent
    /// (<see cref="ReseauNeuronal"/>) : les rayons d'abord, leur indice est celui du pôle, puis les arcs entre voisins.</summary>
    public static (Point Centre, Point[] Poles, Liaison[] Liaisons) Liaisons(Size taille, int nombre, double rayonX, double rayonY, double angleDepart)
    {
        var centre = new Point(taille.Width / 2, taille.Height / 2);
        nombre = Math.Max(0, nombre);
        var poles = Enumerable.Range(0, nombre).Select(i => Position(i, nombre, taille, 0.5, 0.5, rayonX, rayonY, angleDepart)).ToArray();
        // Les rayons, tous courbés du même côté : ils tournent en spirale douce.
        var liaisons = poles.Select(p =>
        {
            var v = p - centre;
            return new Liaison(centre, new Point((centre.X + p.X) / 2 - v.Y * 0.12, (centre.Y + p.Y) / 2 + v.X * 0.12), p, true);
        }).ToList();
        // Les arcs entre pôles voisins, bombés vers l'extérieur : un anneau autour du noyau.
        if (nombre > 2)
            for (var i = 0; i < nombre; i++)
            {
                var a = poles[i];
                var b = poles[(i + 1) % nombre];
                var milieu = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                liaisons.Add(new Liaison(a, milieu + (milieu - centre) * 0.18, b, false));
            }
        return (centre, poles, liaisons.ToArray());
    }
}
