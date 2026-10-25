using System.Windows;
using System.Windows.Media;

namespace DrDxt.App.Effets;

/// <summary>Positions sur l'orbite des pôles, partagées par les rubans de chrome et par le panneau des libellés
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
}
