using System.Runtime.InteropServices;

namespace DrDxt.App;

/// <summary>
/// Les fonctions natives de Windows que plusieurs fichiers de l'App appellent, déclarées une seule fois, aux noms de
/// paramètres du Windows SDK (handleapi.h) : une signature corrigée l'est partout.
/// </summary>
internal static class Natif
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
