using System.Runtime.InteropServices;

namespace DrDxt.Detection.Settings;

public sealed class FirmwareProvider : IFirmwareProvider
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    public bool IsUefi()
    {
        try
        {
            // FirmwareType: 1 = BIOS, 2 = UEFI, 3 = Max
            return GetFirmwareType(out var type) && type == 2u;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
