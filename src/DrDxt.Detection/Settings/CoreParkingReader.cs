using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Lit les cœurs réellement parqués, par l'API CPU Sets de Windows.
///
/// <c>GetSystemCpuSetInformation</c> rend l'index de cœur et le bit <c>Parked</c> de chaque processeur logique (voir
/// <see cref="CpuSetParsing"/> pour le choix de l'API). Contrat : <c>null</c> quand la lecture n'a pas abouti, liste
/// (éventuellement vide) sinon ; « pas pu lire » et « aucun cœur parqué » ne se confondent jamais. Ne lève jamais.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreParkingReader
{
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
