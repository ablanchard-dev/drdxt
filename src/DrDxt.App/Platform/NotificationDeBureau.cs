using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DrDxt.App.Platform;

/// <summary>
/// Une notification de Windows depuis le relevé de fond, qui n'a pas de fenêtre : une icône posée le temps d'une bulle
/// (Shell_NotifyIcon, NIF_INFO), que Windows 10 et 11 montrent comme leurs notifications. L'heure calme de Windows (Assistant de
/// concentration, jeu en plein écran) est respectée : la bulle attend ou ne s'affiche pas. L'icône part après
/// <see cref="Duree"/>. Rien ne lève : une notification refusée ne fait pas tomber le relevé.
/// </summary>
public static class NotificationDeBureau
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);

    public static void Montrer(string titre, string texte)
    {
        HwndSource? fenetre = null;
        var icone = IntPtr.Zero;
        try
        {
            // Une fenêtre à messages seulement : Shell_NotifyIcon demande une fenêtre, le relevé n'en montre aucune.
            fenetre = new HwndSource(new HwndSourceParameters("DrDXT-notification") { ParentWindow = new IntPtr(-3) });
            icone = Environment.ProcessPath is { } exe ? ExtractIconW(IntPtr.Zero, exe, 0) : IntPtr.Zero;
            var donnees = new DonneesDeLIcone
            {
                Taille = (uint)Marshal.SizeOf<DonneesDeLIcone>(),
                Fenetre = fenetre.Handle,
                Id = 1,
                Drapeaux = NifIcon | NifTip | NifInfo,
                Icone = icone,
                Infobulle = "DrDXT",
                Texte = texte.Length > 255 ? texte[..255] : texte,
                Titre = titre.Length > 63 ? titre[..63] : titre,
                DrapeauxDeLaBulle = NiifInfo | NiifRespecterLHeureCalme,
            };
            if (!Shell_NotifyIconW(NimAdd, ref donnees)) { Liberer(fenetre, icone); return; }
            var minuterie = new DispatcherTimer { Interval = Duree };
            var (f, i) = (fenetre, icone);
            minuterie.Tick += (_, _) =>
            {
                minuterie.Stop();
                var retrait = donnees;
                Shell_NotifyIconW(NimDelete, ref retrait);
                Liberer(f, i);
            };
            minuterie.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Notification not shown: {0}", ex);
            Liberer(fenetre, icone);
        }
    }

    private static void Liberer(HwndSource? fenetre, IntPtr icone)
    {
        if (icone != IntPtr.Zero && icone != new IntPtr(1)) DestroyIcon(icone);
        fenetre?.Dispose();
    }

    /// <summary>La taille passée à Windows ; une structure mal décrite fait refuser chaque appel.</summary>
    internal static int TailleDeLaStructure => Marshal.SizeOf<DonneesDeLIcone>();

    private const uint NimAdd = 0, NimDelete = 2;
    private const uint NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const uint NiifInfo = 0x1, NiifRespecterLHeureCalme = 0x80;

    // NOTIFYICONDATAW (shellapi.h), la forme complète depuis Vista.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DonneesDeLIcone
    {
        public uint Taille;
        public IntPtr Fenetre;
        public uint Id;
        public uint Drapeaux;
        public uint MessageDeRappel;
        public IntPtr Icone;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Infobulle;
        public uint Etat;
        public uint MasqueDEtat;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Texte;
        public uint DelaiOuVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Titre;
        public uint DrapeauxDeLaBulle;
        public Guid Element;
        public IntPtr IconeDeLaBulle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref DonneesDeLIcone donnees);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr ExtractIconW(IntPtr instance, string fichier, uint index);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icone);
}
