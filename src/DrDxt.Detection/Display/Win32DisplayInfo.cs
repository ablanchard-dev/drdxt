using System.Runtime.InteropServices;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Settings;

namespace DrDxt.Detection.Display;

// Win32DisplayInfo : la couture de lecture IDisplayInfo (à la place de StubDisplayInfo). Sur la machine de référence,
// un écran capable de 200 Hz piloté à 144 n'avait pour trace qu'un plafond d'images à 141 (la règle basse latence de
// NVIDIA plafonne 3 sous la fréquence). Lecture seule, sans élévation : EnumDisplayDevices + EnumDisplaySettings (user32).
//  1. Le maximum se cherche à la résolution courante : une fréquence plus haute à plus basse résolution inventerait un
//     déficit sur lequel on n'agit qu'en changeant de résolution.
//  2. L'etat du VRR (G-Sync/FreeSync) vit dans le pilote, pas dans Win32 ; la capacite est dans l'EDID, lisible dans le
//     registre sans elevation ni NVAPI (NVIDIA, AMD, Intel). Avec NVAPI, l'etat est lu au pilote
//     (NvAPI_Disp_GetVRRInfo) et SupportsVrr = possible et actif ; sans (AMD, ecran sur une autre carte) : Unknown, et
//     SupportsVrr faux. Sur AMD, FreeSync est lu par ADLX (IADLXDisplayFreeSync), l'ecran retrouve par le modele EDID.
// En session 0 (service), EnumDisplayDevices ne rend rien sans window station : SDK_UNAVAILABLE, jamais « aucun écran ».
public sealed class Win32DisplayInfo : IDisplayInfo
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const int DisplayDevicePrimaryDevice = 0x00000004;
    private const int DisplayDeviceMirroringDriver = 0x00000008;

    /// <summary>
    /// Fait rendre a <c>DeviceID</c> le nom d'interface du moniteur, celui qui porte les deux
    /// champs de la cle de registre : <c>DISPLAY#AUS2413#5&amp;3ac6cfcf&amp;0&amp;UID4354#{guid}</c>.
    /// Sans ce drapeau, un chemin de la classe MONITOR, qui ne mene pas a l'EDID.
    /// </summary>
    private const uint EddGetDeviceInterfaceName = 0x00000001;

    private readonly DrDxt.GpuNative.NvApi.INvApiNative? _nvapi;
    private readonly DrDxt.Detection.Gpu.Adlx.IAdlxNative? _adlx;

    public Win32DisplayInfo() : this(null) { }

    /// <param name="nvapi">Pour lire G-Sync au pilote. null = VRR non lu (Unknown).</param>
    /// <param name="adlx">Pour lire FreeSync au pilote AMD. null = non lu.</param>
    public Win32DisplayInfo(DrDxt.GpuNative.NvApi.INvApiNative? nvapi, DrDxt.Detection.Gpu.Adlx.IAdlxNative? adlx = null)
    {
        _nvapi = nvapi;
        _adlx = adlx;
    }

    private (TriState Possible, TriState Enabled) LireVrr(string deviceName,
        IReadOnlyList<DrDxt.Detection.Gpu.Adlx.AdlxEcranFreeSync> freeSync)
    {
        var pilote = _nvapi is not null && _nvapi.TryReadVrr(deviceName, out var r)
            ? Traduire(r)
            : FreeSyncDe(freeSync, NomDuModele(EdidDuMoniteur.Lire(NomInterfaceDuMoniteur(deviceName))));

        // L'EDID repond la ou le pilote ne peut pas. Mesure du 18/09/2026 : le pilote rend « possible = 0 » avec G-Sync
        // coupe globalement, qu'on ne lit pas « ecran incapable » ; sans l'EDID, `Possible` resterait Unknown et les deux
        // leviers de latence se tairaient sur un ecran a 200 Hz qui declare FreeSync. Seulement si le pilote n'a rien su
        // dire : sa lecture connait l'etat en plus.
        if (pilote.Possible != TriState.Unknown)
            return pilote;

        var capacite = EdidVrrCapabilite.Lire(EdidDuMoniteur.Lire(NomInterfaceDuMoniteur(deviceName)));
        return (capacite, pilote.Enabled);
    }

    /// <summary>Le nom d'interface du moniteur branche sur cet adaptateur, ou <c>null</c> : il designe l'ecran actif, le
    /// registre gardant les branchements passes (quatre entrees pour deux ecrans sur la machine de reference).</summary>
    private static string? NomInterfaceDuMoniteur(string deviceName)
    {
        var moniteur = new DisplayDevice();
        moniteur.cb = Marshal.SizeOf<DisplayDevice>();
        return EnumDisplayDevices(deviceName, 0, ref moniteur, EddGetDeviceInterfaceName)
            ? moniteur.DeviceID
            : null;
    }

    /// <summary>
    /// A-FS1 : l'etat FreeSync de l'ecran que le pilote AMD nomme comme l'EDID. Un nom absent, ou porte par deux
    /// ecrans, n'est pas une lecture : Unknown, jamais un ecran pris au hasard.
    /// </summary>
    public static (TriState Possible, TriState Enabled) FreeSyncDe(
        IReadOnlyList<DrDxt.Detection.Gpu.Adlx.AdlxEcranFreeSync> ecrans, string? nomDuModele)
    {
        var memes = nomDuModele is { Length: > 0 }
            ? ecrans.Where(e => string.Equals(e.Nom.Trim(), nomDuModele, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (memes.Count != 1) return (TriState.Unknown, TriState.Unknown);
        var e = memes[0];
        return (e.Supporte switch { true => TriState.On, false => TriState.Off, null => TriState.Unknown },
                e.Supporte == true
                    ? e.Actif switch { true => TriState.On, false => TriState.Off, null => TriState.Unknown }
                    : TriState.Unknown);
    }

    /// <summary>Le nom de modele ecrit dans l'EDID (descripteur 0xFC, octets 5 a 17), ou null.</summary>
    public static string? NomDuModele(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (var d = 54; d <= 108; d += 18)
        {
            if (edid[d] != 0 || edid[d + 1] != 0 || edid[d + 3] != 0xFC) continue;
            var nom = System.Text.Encoding.ASCII.GetString(edid, d + 5, 13).Split('\n')[0].Trim();
            return nom.Length > 0 ? nom : null;
        }
        return null;
    }

    /// <summary>
    /// Ce qu'une lecture du pilote autorise a dire. Mesure du 10/09/2026 sur la machine de reference : « possible = 0 »
    /// sur les deux ecrans, et « Enable G-SYNC globally » (DRS VRR_MODE 0x1194F158) = 0 ; G-Sync coupe, le bit ne
    /// distingue pas un ecran incapable d'un capable eteint, et y lire « non capable » tairait le vrai constat
    /// « G-Sync desactive » sur un ecran FreeSync en DisplayPort. « Possible » ne vaut Off que si le VRR est active.
    /// </summary>
    public static (TriState Possible, TriState Enabled) Traduire(DrDxt.GpuNative.NvApi.VrrReading r) =>
        (r.Possible ? TriState.On : r.Enabled ? TriState.Off : TriState.Unknown,
         r.Enabled ? TriState.On : TriState.Off);

    public Result<DisplayInfo> GetPrimaryGameDisplay()
    {
        var all = GetAllDisplays();
        if (!all.IsOk) return Result<DisplayInfo>.Fail(all.Error!.Value.Code, all.Error.Value.Message);

        var primary = all.Value!.FirstOrDefault(d => d.IsPrimaryGameDisplay);
        return primary is null
            // Aucun écran principal parmi les écrans branchés : machine incomprise, on passe sans deviner.
            ? Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable,
                "No primary display among the attached displays.")
            : Result<DisplayInfo>.Ok(primary);
    }

    public Result<IReadOnlyList<DisplayInfo>> GetAllDisplays()
    {
        try
        {
            var displays = new List<DisplayInfo>();

            var device = new DisplayDevice();
            device.cb = Marshal.SizeOf<DisplayDevice>();
            // Lu une fois par enumeration ; sans carte AMD, ADLX n'est pas disponible et la liste reste vide.
            var freeSync = _adlx is { IsAvailable: true } a ? a.LireFreeSync() : [];

            for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
            {
                var flags = device.StateFlags;
                device.cb = Marshal.SizeOf<DisplayDevice>(); // remis à zéro pour l'appel suivant

                // Un pilote miroir n'est pas un écran sur lequel on joue.
                if ((flags & DisplayDeviceAttachedToDesktop) == 0) continue;
                if ((flags & DisplayDeviceMirroringDriver) != 0) continue;

                var name = device.DeviceName;
                var current = new DevMode();
                current.dmSize = (ushort)Marshal.SizeOf<DevMode>();
                if (!EnumDisplaySettings(name, EnumCurrentSettings, ref current)) continue;

                var vrr = LireVrr(name, freeSync);
                displays.Add(new DisplayInfo(
                    IsPrimaryGameDisplay: (flags & DisplayDevicePrimaryDevice) != 0,
                    // Lu au pilote quand NVAPI est la : voir l'en-tete. Non lu => faux, et Unknown.
                    SupportsVrr: vrr.Possible == TriState.On && vrr.Enabled == TriState.On,
                    RefreshHz: FrequenceLue(current.dmDisplayFrequency),
                    MaxRefreshHz: MaxRefreshAtResolution(
                        name, current.dmPelsWidth, current.dmPelsHeight, current.dmBitsPerPel),
                    DeviceName: name,
                    VrrPossible: vrr.Possible,
                    VrrEnabled: vrr.Enabled,
                    Largeur: current.dmPelsWidth,
                    Hauteur: current.dmPelsHeight,
                    NomInterface: NomInterfaceDuMoniteur(name),
                    Adaptateur: device.DeviceString));
            }

            // Zéro écran = bureau invisible (session 0 / pas de window station), pas une machine sans écran.
            if (displays.Count == 0)
                return Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "No display could be enumerated (no window station?).");

            // Le principal d'abord : l'appelant qui prend la tête obtient l'écran visé par un jeu plein écran.
            var ordered = displays
                .OrderByDescending(d => d.IsPrimaryGameDisplay)
                .ToList();

            return Result<IReadOnlyList<DisplayInfo>>.Ok(ordered);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Display enumeration failed: {ex.Message}");
        }
    }

    /// <summary>La plus haute fréquence que Windows propose à une résolution donnée (la
    /// native de l'écran, comparée à son EDID), en 32 bits par pixel. 0 = aucun mode.</summary>
    public static int MaxRefreshAt(string deviceName, int width, int height) => MaxRefreshAtResolution(deviceName, width, height, 32);

    /// <summary>Deux moniteurs actifs sur la même sortie de bureau = écrans dupliqués ; Windows n'y
    /// propose que les modes communs.</summary>
    public static bool Duplique(string deviceName)
    {
        var moniteur = new DisplayDevice();
        moniteur.cb = Marshal.SizeOf<DisplayDevice>();
        return EnumDisplayDevices(deviceName, 1, ref moniteur, 0) && (moniteur.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
    }

    /// <summary>Win32 rend 0 ou 1 pour « la fréquence par défaut du matériel » (DEVMODE) : ce n'est pas une fréquence, elle
    /// sort 0 (non lue), comme une lecture ratée.</summary>
    internal static int FrequenceLue(int dmDisplayFrequency) => dmDisplayFrequency > 1 ? dmDisplayFrequency : 0;

    /// <summary>La fréquence la plus haute que l'écran offre à cette résolution et cette profondeur de couleur, pas celle
    /// d'un mode 800x600 (un déficit sans geste possible). 0 si aucun mode.</summary>
    private static int MaxRefreshAtResolution(string deviceName, int width, int height, int bpp)
    {
        var max = 0;
        var mode = new DevMode();
        mode.dmSize = (ushort)Marshal.SizeOf<DevMode>();

        for (int i = 0; EnumDisplaySettings(deviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height && mode.dmBitsPerPel == bpp
                && FrequenceLue(mode.dmDisplayFrequency) > max)
                max = mode.dmDisplayFrequency;

            mode.dmSize = (ushort)Marshal.SizeOf<DevMode>();
        }

        return max;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettings(
        string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public uint dmDisplayFlags;
        public int dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
