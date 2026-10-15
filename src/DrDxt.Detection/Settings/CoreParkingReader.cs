using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Lit les cœurs réellement parqués, par l'API CPU Sets de Windows.
///
/// <c>GetSystemCpuSetInformation</c> rend, pour chaque processeur logique, son index de cœur et un
/// bit <c>Parked</c>. Documentée, en espace utilisateur, <b>sans élévation</b> et sans dépendance
/// nouvelle ; le compteur de performance équivalent porte un nom localisé (« État de parcage » sur
/// un Windows français), ce qui obligerait à résoudre les noms par index et à ajouter un paquet.
///
/// Contrat : <c>null</c> quand la lecture n'a pas abouti, liste (éventuellement vide) sinon. Cette
/// distinction est la raison d'être du fichier : un lecteur qui rendrait toujours une liste
/// vide rendrait « je n'ai pas pu lire » indiscernable de « aucun cœur n'est parqué », et deux
/// consommateurs en tireraient des conclusions opposées, dont une qui proposerait une écriture.
///
/// Ne lève jamais : tout échec natif devient <c>null</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreParkingReader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    /// <summary>Garde-fou de taille : au-delà, on refuse d'allouer plutôt que de faire confiance
    /// à une valeur rendue par un appel qui vient d'échouer.</summary>
    private const uint MaxBufferBytes = 1 << 20;

    public static IReadOnlyList<int>? Read()
    {
        try
        {
            // Premier appel : la taille nécessaire. Il échoue par construction (tampon nul), donc
            // son faux retour n'est pas un signal : seule la taille compte.
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var needed, IntPtr.Zero, 0);
            if (needed == 0 || needed > MaxBufferBytes)
                return null;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out var written, IntPtr.Zero, 0))
                    return null;
                if (written == 0 || written > needed)
                    return null;

                var managed = new byte[written];
                Marshal.Copy(buffer, managed, 0, (int)written);
                return CpuSetParsing.ParkedCoreIndexes(managed, (int)written);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Dernier filet : aucune exception ne doit traverser un lecteur de diagnostic.
            return null;
        }
    }
}
