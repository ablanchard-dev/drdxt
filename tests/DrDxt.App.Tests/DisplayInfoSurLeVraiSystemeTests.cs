using System.Runtime.InteropServices;
using DrDxt.Detection.Display;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 26/09/2026 : les fréquences d'écran décident du plafond d'images et du conseil VRR ; le lecteur
// (EnumDisplayDevices + EnumDisplaySettings) n'était exercé par aucun test sur le vrai système. Il est
// confronté à une autre API de Windows (EnumDisplayMonitors + GetMonitorInfo) : même nombre d'écrans, même
// écran principal, et des fréquences cohérentes (courante positive, jamais au-dessus du maximum lu).
public sealed class DisplayInfoSurLeVraiSystemeTests
{
    [Fact]
    [Trait("Category", "Machine")] // lit les écrans de la vraie session : un runner CI peut n'en avoir aucun
    public void Les_ecrans_lus_sont_ceux_que_Windows_enumere()
    {
        var lus = new Win32DisplayInfo().GetAllDisplays();
        lus.IsOk.Should().BeTrue();

        var moniteurs = new List<(string Nom, bool Principal)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(m, ref mi)) moniteurs.Add((mi.szDevice, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        moniteurs.Should().NotBeEmpty("temoin : Windows énumère au moins un écran");
        lus.Value!.Should().HaveCount(moniteurs.Count);
        lus.Value!.Count(d => d.IsPrimaryGameDisplay).Should().Be(1);
        lus.Value!.Single(d => d.IsPrimaryGameDisplay).DeviceName.Should().Be(moniteurs.Single(m => m.Principal).Nom);
        foreach (var d in lus.Value!)
        {
            d.RefreshHz.Should().BePositive();
            if (d.MaxRefreshHz > 0) d.RefreshHz.Should().BeLessThanOrEqualTo(d.MaxRefreshHz);
        }
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}
