using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Le relevé tourne aussi DrDXT fermé (lancé à l'ouverture de session, sans fenêtre).
/// L'App ouverte en même temps relèverait la même partie une seconde fois : un seul relevé à la fois, celui qui tient ce
/// verrou. Un fichier ouvert sans partage, pas un mutex : il se tient à travers les <c>await</c> et Windows le rend à la
/// mort du processus.
/// </summary>
public static class VerrouDuReleve
{
    public static string CheminParDefaut() => FichierJsonAtomique.CheminLocal("releve-parties.verrou");

    /// <summary>Le verrou, ou <c>null</c> s'il est déjà tenu (ou illisible).</summary>
    public static IDisposable? Prendre(string chemin)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            return new FileStream(chemin, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Veille seulement en tenant le verrou ; sinon réessaie toutes les trente secondes, jusqu'à l'annulation.</summary>
    public static async Task VeillerSeulAsync(Func<IDisposable?> prendre, Func<CancellationToken, Task> veiller,
        Func<TimeSpan, CancellationToken, Task> patienter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var verrou = prendre())
            {
                if (verrou is not null)
                {
                    await veiller(ct);
                    return;
                }
            }
            try { await patienter(VeilleDesParties.HorsPartie, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
