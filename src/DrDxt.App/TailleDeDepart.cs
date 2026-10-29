namespace DrDxt.App;

/// <summary>
/// La taille a laquelle la fenetre s'ouvre, adaptee a la place reellement disponible. `MainWindow.xaml` demande
/// 1320 x 900 DIP, et WPF deborde au lieu de rapetisser : un portable 1366 x 768 laisse ~728 DIP de haut sans la barre
/// des taches (172 sous l'ecran), un 1920 x 1080 a 150 % (recommande sur beaucoup de portables) ~1280 x 673. Ce qui
/// depasse en bas, c'est l'action de l'ecran.
/// Pure et sans WPF, prouvee sans fenetre : `MainWindow` passe `SystemParameters.WorkArea`, les tests des chiffres. Le
/// minimum l'emporte : sur un ecran plus petit, rapetisser rendrait l'interface inutilisable ; l'utilisateur deplace la
/// fenetre.
/// </summary>
public static class TailleDeDepart
{
    /// <summary>Doit rester egal a `MinWidth` de MainWindow.xaml : un test tient les deux.</summary>
    public const double MinimumLargeur = 1040;

    /// <summary>Doit rester egal a `MinHeight` de MainWindow.xaml.</summary>
    public const double MinimumHauteur = 640;

    public static (double Largeur, double Hauteur) Adaptee(
        double voulueLargeur, double voulueHauteur, double zoneLargeur, double zoneHauteur)
        => (Borner(voulueLargeur, zoneLargeur, MinimumLargeur),
            Borner(voulueHauteur, zoneHauteur, MinimumHauteur));

    /// <summary>Le coin haut-gauche : centré dans la zone de travail, sans jamais remonter au-dessus de son bord haut ni sortir
    /// à gauche. Quand le minimum déborde un petit écran, la barre de titre (déplacer, fermer) reste visible : c'est le bas qui
    /// déborde.</summary>
    public static (double Gauche, double Haut) Position(
        double largeur, double hauteur, double zoneGauche, double zoneHaut, double zoneLargeur, double zoneHauteur)
        => (zoneGauche + Math.Max(0, (zoneLargeur - largeur) / 2), zoneHaut + Math.Max(0, (zoneHauteur - hauteur) / 2));

    private static double Borner(double voulue, double zone, double minimum)
    {
        // Une zone de travail rapportee a 0 (sortie de veille, branchement d'un second
        // moniteur) ne doit pas donner une fenetre invisible.
        if (zone <= 0) return minimum;
        return Math.Max(minimum, Math.Min(voulue, zone));
    }
}
