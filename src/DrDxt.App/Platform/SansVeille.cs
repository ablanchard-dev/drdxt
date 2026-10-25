using System.Runtime.InteropServices;

namespace DrDxt.App.Platform;

/// <summary>
/// Empêche la mise en veille tant qu'il est ouvert : une demande d'alimentation « système requis » de Windows, tenue par son
/// handle et pas par un fil (la recherche de sous-tension change de fil à chaque attente). Si l'App meurt, Windows retire la
/// demande avec le processus. Refusée par Windows, la veille reste possible : rien ne lève.
/// </summary>
public sealed class SansVeille : IDisposable
{
    private IntPtr _demande;

    private SansVeille(IntPtr demande) => _demande = demande;

    public static SansVeille Ouvrir()
    {
        var raison = new RaisonDeLaDemande { Version = 0, Flags = ChaineSimple, Texte = "DrDXT: undervolt stability test running" };
        var demande = PowerCreateRequest(ref raison);
        if (demande == InvalidHandle) return new SansVeille(IntPtr.Zero);
        if (!PowerSetRequest(demande, SystemeRequis))
        {
            Natif.CloseHandle(demande);
            return new SansVeille(IntPtr.Zero);
        }
        return new SansVeille(demande);
    }

    /// <summary>La demande est-elle posée ? Faux quand Windows l'a refusée.</summary>
    public bool Active => _demande != IntPtr.Zero;

    public void Dispose()
    {
        var demande = Interlocked.Exchange(ref _demande, IntPtr.Zero);
        if (demande == IntPtr.Zero) return;
        PowerClearRequest(demande, SystemeRequis);
        Natif.CloseHandle(demande);
    }

    private const uint ChaineSimple = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
    private const int SystemeRequis = 1; // PowerRequestSystemRequired
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RaisonDeLaDemande
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Texte;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr PowerCreateRequest(ref RaisonDeLaDemande raison);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr demande, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr demande, int type);
}
