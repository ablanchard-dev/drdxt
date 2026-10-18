using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>La mémoire telle que Windows la voit à l'instant (GlobalMemoryStatusEx, kernel32).
/// Lecture seule, sans droits particuliers. Un échec rend null : le constat se dit non lu, jamais sain.</summary>
public static class PressionMemoire
{
    public static MemoireLue? Lire()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return null;
            // ullTotalPageFile est la limite d'engagement courante ; l'engagement est ce qu'il en reste à retirer.
            return new MemoireLue(m.ullTotalPhys, m.ullAvailPhys, m.ullTotalPageFile - m.ullAvailPageFile, m.ullTotalPageFile);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Les lectures de pages dures par seconde (compteur Memory\Page Reads/sec), mesurées entre deux relevés
    /// bruts espacés de <paramref name="ecart"/> : delta du compteur sur delta de l'horloge du compteur (Microsoft Learn,
    /// « Win32_PerfRawData_PerfOS_Memory », PERF_COUNTER_COUNTER). <c>null</c> = non lu.</summary>
    public static double? LecturesDuresParSeconde(TimeSpan ecart)
    {
        static (ulong N, ulong T, ulong F)? Releve()
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT PageReadsPersec, Timestamp_PerfTime, Frequency_PerfTime FROM Win32_PerfRawData_PerfOS_Memory");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    return (Convert.ToUInt64(o["PageReadsPersec"], System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToUInt64(o["Timestamp_PerfTime"], System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToUInt64(o["Frequency_PerfTime"], System.Globalization.CultureInfo.InvariantCulture));
            return null;
        }
        try
        {
            if (Releve() is not { } a) return null;
            Thread.Sleep(ecart);
            if (Releve() is not { } b || b.T <= a.T || a.F == 0 || b.N < a.N) return null;
            return (b.N - a.N) / ((double)(b.T - a.T) / a.F);
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
