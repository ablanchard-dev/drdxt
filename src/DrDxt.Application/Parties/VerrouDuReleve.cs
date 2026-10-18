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
            // Dossier du joueur : ouvert sans suivre de point de reparse, la suppression à la fermeture ne retire que ce nom,
            // jamais la cible d'un lien posé d'avance (l'App peut tourner élevée).
            var h = CreateFileW(chemin, LireEcrireSupprimer, FileShare.None, IntPtr.Zero, FileMode.OpenOrCreate,
                SupprimerALaFermeture | OuvrirLePointDeReparse, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new FileStream(h, FileAccess.ReadWrite);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // GENERIC_READ | GENERIC_WRITE | DELETE ; FILE_FLAG_DELETE_ON_CLOSE, FILE_FLAG_OPEN_REPARSE_POINT.
    private const uint LireEcrireSupprimer = 0xC0010000, SupprimerALaFermeture = 0x04000000, OuvrirLePointDeReparse = 0x00200000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite,
        FileMode creation, uint drapeaux, IntPtr modele);

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
