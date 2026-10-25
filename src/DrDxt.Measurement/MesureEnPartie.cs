namespace DrDxt.Measurement;

/// <summary>
/// Une capture prise en pleine partie par le relevé de fond, fenêtre après fenêtre. Le banc n'y cherche pas de démarrage
/// (secondes de chargement à couper) : au début d'une minute de jeu, les secondes lentes sont du jeu, souvent ses pires
/// images. La portée suit le fil asynchrone, comme <see cref="DrDxt.Core.SansGesteDuJoueur"/>, et traverse les décorateurs
/// du banc sans qu'ils aient à la porter.
/// </summary>
public static class MesureEnPartie
{
    private static readonly AsyncLocal<bool> Portee = new();

    public static bool Active => Portee.Value;

    public static IDisposable Ouvrir()
    {
        var avant = Portee.Value;
        Portee.Value = true;
        return new Fermeture(avant);
    }

    private sealed class Fermeture(bool avant) : IDisposable
    {
        public void Dispose() => Portee.Value = avant;
    }
}
