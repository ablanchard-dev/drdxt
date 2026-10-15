using System.Runtime.InteropServices;

namespace DrDxt.Measurement;

/// <summary>
/// La pression de validation (commit) de Windows : octets valides / limite de validation,
/// dans [0, 1].
///
/// C'est la seule grandeur du classifieur de goulot qui ne vient pas de PresentMon, et la seule
/// qui parle de fiabilite plutot que de FPS. Microsoft documente le fait qui la rend utile :
/// quand la validation atteint la limite, le systeme et les processus peuvent geler, planter ou
/// mal fonctionner. Sur une machine dans cet etat, aucun reglage graphique ne repare quoi que
/// ce soit, et lui vendre un overclock serait vendre la mauvaise chose.
/// Source : https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
///
/// Rend <c>null</c> quand la lecture echoue. Jamais 0 : « je n'ai pas pu lire » et « la machine
/// respire » sont deux etats differents, et le second desarmerait a tort le diagnostic memoire.
/// </summary>
public static class CommitPressureReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        // ullTotalPageFile / ullAvailPageFile sont mal nommes : malgre leur nom, Windows y met
        // la limite de validation et ce qu'il en reste (RAM + fichier d'echange), pas le seul
        // fichier d'echange. C'est exactement le couple dont on a besoin, et c'est le piege de
        // lecture le plus courant sur cette API.
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>La mémoire physique libre, en octets, ou null si elle n'est pas lisible.</summary>
    public static ulong? LibrePhysique()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : null;
        }
        catch { return null; }
    }

    /// <summary>La pression de validation actuelle dans [0, 1], ou null si elle n'est pas lisible.</summary>
    public static double? Read()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return null;

            var limit = status.ullTotalPageFile;
            if (limit == 0) return null;
            if (status.ullAvailPageFile > limit) return null; // incoherent → on ne devine pas

            var used = limit - status.ullAvailPageFile;
            return (double)used / limit;
        }
        catch
        {
            // Fail-clean comme partout ailleurs : une mesure indisponible ne casse pas un scan.
            return null;
        }
    }
}
