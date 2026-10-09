using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>BUREAU (09/10/2026) : la mémoire telle que Windows la voit à l'instant (GlobalMemoryStatusEx, kernel32).
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
