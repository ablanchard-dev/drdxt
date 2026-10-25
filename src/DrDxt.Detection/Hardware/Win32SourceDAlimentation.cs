using System.Runtime.InteropServices;
using DrDxt.Application.Tweaks.Hardware;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Secteur ou batterie, lu par GetSystemPowerStatus (kernel32) : ACLineStatus vaut 0 sur batterie, 1 sur secteur, 255
/// quand Windows ne sait pas (Microsoft Learn, SYSTEM_POWER_STATUS). Un bureau sans batterie dit 1.
/// </summary>
public sealed class Win32SourceDAlimentation : ISourceDAlimentation
{
    public bool? SurBatterie() => Interpreter(GetSystemPowerStatus(out var etat) ? etat.ACLineStatus : (byte)255);

    /// <summary>Pur : la valeur ACLineStatus lue.</summary>
    public static bool? Interpreter(byte acLineStatus) => acLineStatus switch
    {
        0 => true,
        1 => false,
        _ => null,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS etat);
}
