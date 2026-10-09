using System.Runtime.InteropServices;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Settings;

namespace DrDxt.Detection.Display;

// -----------------------------------------------------------------------
// Win32DisplayInfo — the real IDisplayInfo read seam, replacing StubDisplayInfo.
//
// Until now the display seam was a fail-clean stub, so the product could not read a refresh
// rate at all. That gap had a cost, found on the author's own machine: a panel capable of
// 200 Hz was being driven at 144, and the only visible trace was a frame cap sitting at 141
// (the NVIDIA low-latency rule caps 3 under the refresh). The product read that 141 for
// months, as a revert value, and never said a word about it.
//
// Read-only, non-elevated, no dependency: EnumDisplayDevices + EnumDisplaySettings, both in
// user32. Nothing here writes.
//
// Two rules this file exists to enforce:
//  1. The maximum is looked up at the current resolution. A panel often offers a higher
//     refresh at a lower resolution; reporting that as "the maximum" would invent a deficit
//     the user cannot act on without changing resolution.
//  2. L'etat du VRR n'est pas detectable par Win32 — G-Sync/FreeSync vit dans le pilote.
//     18/09/2026 : cette ligne disait « VRR is NOT detectable through Win32 », sans distinguer
//     l'etat de la capacite. La capacite, elle, est ecrite dans l'EDID de l'ecran, lisible dans le
//     registre sans elevation et sans NVAPI — donc valable NVIDIA, AMD et Intel. La phrase trop
//     large a laisse `VrrCapableDisplay` a Unknown pendant des mois, et deux leviers muets avec.
//     L8 suite (10/09/2026) : quand NVAPI est branche, il est LU au pilote (NvAPI_Disp_GetVRRInfo)
//     et SupportsVrr = possible et actif. Sans NVAPI (AMD, ecran sur une autre carte) : Unknown,
//     et SupportsVrr reste faux — un manque dit, pas une lecture.
//     A-FS1 (08/10/2026) : sur AMD, FreeSync est lu au pilote par ADLX (IADLXDisplayFreeSync), l'ecran
//     retrouve par le nom de modele de son EDID.
//
// Not executable in a service / SESSION 0: EnumDisplayDevices returns nothing without a
// window station. That is not an error to paper over — it is reported as SDK_UNAVAILABLE so
// the caller skips cleanly instead of concluding "no displays".
// -----------------------------------------------------------------------
public sealed class Win32DisplayInfo : IDisplayInfo
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const int DisplayDevicePrimaryDevice = 0x00000004;
    private const int DisplayDeviceMirroringDriver = 0x00000008;

    /// <summary>
    /// Fait rendre a <c>DeviceID</c> le nom d'INTERFACE du moniteur, celui qui porte les deux
    /// champs de la cle de registre : <c>DISPLAY#AUS2413#5&amp;3ac6cfcf&amp;0&amp;UID4354#{guid}</c>.
    /// Sans ce drapeau, <c>DeviceID</c> rend un chemin de la classe MONITOR, qui ne permet pas de
    /// retrouver l'EDID.
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

        // L'EDID repond la ou le pilote ne peut pas. Mesure du 18/09/2026 : sur cette machine
        // le pilote rend « possible = 0 » avec G-Sync coupe globalement, et ce fichier refuse a
        // juste titre d'y lire « ecran incapable » — donc `Possible` restait Unknown, et les deux
        // leviers de latence se taisaient sur un ecran de jeu a 200 Hz qui declare FreeSync.
        //
        // La capacite d'un ecran est ecrite dans son EDID, independamment de l'etat du pilote et
        // du vendeur : l'en-tete de ce fichier disait « VRR is NOT detectable through Win32 »,
        // ce qui est vrai de l'etat et faux de la capacite. On ne s'en sert que si le pilote n'a
        // rien su dire — une lecture au pilote connait l'etat en plus, elle reste prioritaire.
        if (pilote.Possible != TriState.Unknown)
            return pilote;

        var capacite = EdidVrrCapabilite.Lire(EdidDuMoniteur.Lire(NomInterfaceDuMoniteur(deviceName)));
        return (capacite, pilote.Enabled);
    }

    /// <summary>
    /// Le nom d'interface du moniteur branche sur cet adaptateur, ou <c>null</c>.
    ///
    /// C'est lui qui designe l'ecran actif : le registre conserve aussi les branchements passes
    /// (quatre entrees pour deux ecrans sur la machine de reference), et lire le premier venu
    /// rendrait la capacite d'un ecran debranche.
    /// </summary>
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
    /// Ce qu'une lecture du pilote AUTORISE a dire.
    ///
    /// Mesure du 10/09/2026 sur la machine de reference : « possible = 0 » sur les deux ecrans, ET
    /// « Enable G-SYNC globally » (DRS VRR_MODE 0x1194F158) = 0. Avec G-Sync coupe globalement, le bit
    /// « possible » ne distingue pas un ecran incapable d'un ecran capable dont le VRR est eteint : lire
    /// ce 0 comme « non capable » aurait fait taire le constat « G-Sync desactive » — un vrai, sur un
    /// ecran FreeSync branche en DisplayPort. « Possible » ne vaut Off que si le VRR est ACTIVE.
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
            // No primary among attached displays is not a machine we understand: skip, never guess.
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
                device.cb = Marshal.SizeOf<DisplayDevice>(); // reset for the next call

                // A mirroring driver is not a panel someone plays on.
                if ((flags & DisplayDeviceAttachedToDesktop) == 0) continue;
                if ((flags & DisplayDeviceMirroringDriver) != 0) continue;

                var name = device.DeviceName;
                var current = new DevMode();
                current.dmSize = (ushort)Marshal.SizeOf<DevMode>();
                if (!EnumDisplaySettings(name, EnumCurrentSettings, ref current)) continue;

                var vrr = LireVrr(name, freeSync);
                displays.Add(new DisplayInfo(
                    IsPrimaryGameDisplay: (flags & DisplayDevicePrimaryDevice) != 0,
                    // Lu au pilote quand NVAPI est la — voir l'en-tete. Non lu => faux, et Unknown.
                    SupportsVrr: vrr.Possible == TriState.On && vrr.Enabled == TriState.On,
                    RefreshHz: current.dmDisplayFrequency,
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

            // Zero displays means we could not see the desktop (session 0 / no window station),
            // not that the machine has no screen. Saying "none" would be a fabricated reading.
            if (displays.Count == 0)
                return Result<IReadOnlyList<DisplayInfo>>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "No display could be enumerated (no window station?).");

            // Primary first — callers that take the head get the screen a fullscreen game targets.
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

    /// <summary>
    /// Highest refresh the panel offers AT this resolution and colour depth. Scanning every mode
    /// and taking the global maximum would report the refresh of some 800x600 mode as "what your
    /// screen can do" — a deficit the user cannot act on. 0 when no mode enumerates.
    /// </summary>
    /// <summary>06/10/2026 (chantier B2) : la plus haute fréquence que Windows propose à une résolution donnée (la
    /// native de l'écran, comparée à son EDID), en 32 bits par pixel. 0 = aucun mode.</summary>
    public static int MaxRefreshAt(string deviceName, int width, int height) => MaxRefreshAtResolution(deviceName, width, height, 32);

    /// <summary>Revue F-15 : deux moniteurs actifs sur la même sortie de bureau = écrans dupliqués ; Windows n'y
    /// propose que les modes communs.</summary>
    public static bool Duplique(string deviceName)
    {
        var moniteur = new DisplayDevice();
        moniteur.cb = Marshal.SizeOf<DisplayDevice>();
        return EnumDisplayDevices(deviceName, 1, ref moniteur, 0) && (moniteur.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
    }

    private static int MaxRefreshAtResolution(string deviceName, int width, int height, int bpp)
    {
        var max = 0;
        var mode = new DevMode();
        mode.dmSize = (ushort)Marshal.SizeOf<DevMode>();

        for (int i = 0; EnumDisplaySettings(deviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height && mode.dmBitsPerPel == bpp
                && mode.dmDisplayFrequency > max)
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
