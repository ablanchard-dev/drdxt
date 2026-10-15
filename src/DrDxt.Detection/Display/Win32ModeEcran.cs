using System.Runtime.InteropServices;
using DrDxt.Application.Tweaks.Hardware;

namespace DrDxt.Detection.Display;

/// <summary>
/// Le mode d'un écran, lu par EnumDisplaySettings et posé par ChangeDisplaySettingsEx (user32). Sans droits :
/// sans CDS_GLOBAL, CDS_UPDATEREGISTRY range le mode dans le profil de l'utilisateur (Microsoft Learn). Le mode posé
/// part du mode courant, dont seule la fréquence change ; résolution et profondeur sont redites pour que le pilote ne
/// choisisse pas à notre place.
/// </summary>
public sealed class Win32ModeEcran : IModeEcran
{
    private const int EnumCurrentSettings = -1;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsTest = 0x00000002;
    private const uint DmBitsPerPel = 0x00040000;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const int DispChangeFailed = -1;

    public ModeEcran? Courant(string peripherique)
        => Lire(peripherique) is { } m ? new ModeEcran(peripherique, m.dmPelsWidth, m.dmPelsHeight, m.dmBitsPerPel, m.dmDisplayFrequency) : null;

    public int Tester(ModeEcran mode) => Poser(mode, CdsTest);

    public int Ecrire(ModeEcran mode, bool enregistrer) => Poser(mode, enregistrer ? CdsUpdateRegistry : 0);

    private static int Poser(ModeEcran mode, uint drapeaux)
    {
        if (Lire(mode.Peripherique) is not { } m) return DispChangeFailed;
        m.dmPelsWidth = mode.Largeur;
        m.dmPelsHeight = mode.Hauteur;
        m.dmBitsPerPel = mode.Bpp;
        m.dmDisplayFrequency = mode.Hz;
        m.dmFields = DmPelsWidth | DmPelsHeight | DmBitsPerPel | DmDisplayFrequency;
        return ChangeDisplaySettingsEx(mode.Peripherique, ref m, IntPtr.Zero, drapeaux, IntPtr.Zero);
    }

    private static Win32DisplayInfo.DevMode? Lire(string peripherique)
    {
        var m = new Win32DisplayInfo.DevMode { dmSize = (ushort)Marshal.SizeOf<Win32DisplayInfo.DevMode>() };
        return Win32DisplayInfo.EnumDisplaySettings(peripherique, EnumCurrentSettings, ref m) ? m : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string lpszDeviceName, ref Win32DisplayInfo.DevMode lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}
