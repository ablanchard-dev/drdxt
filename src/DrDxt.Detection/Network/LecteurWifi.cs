using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Network;

/// <summary>
/// La qualité de la connexion Wi-Fi, lue sans la localisation. Microsoft Learn (« Changes to API behavior
/// for Wi-Fi access and location ») soumet à l'accord de localisation WlanQueryInterface avec current_connection, la liste
/// des réseaux et le scan ; DrDXT n'appelle aucun des trois. Il lit wlan_intf_opcode_realtime_connection_quality (19) et
/// wlan_intf_opcode_channel_number (8), dans l'ordre de l'énumération de wlanapi.h (SDK 10.0.26100). Lecture seule ;
/// <c>null</c> = pas de Wi-Fi connecté, ou rien de lisible (Windows antérieur à cette lecture).
/// </summary>
public static class LecteurWifi
{
    private const uint OpcodeCanal = 8;
    private const uint OpcodeQualiteTempsReel = 19;
    private const int EtatConnecte = 1; // wlan_interface_state_connected

    public static QualiteWifi? Lire()
    {
        IntPtr handle = IntPtr.Zero, liste = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return null;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out liste) != 0 || liste == IntPtr.Zero) return null;

            var nombre = Marshal.ReadInt32(liste);
            var taille = Marshal.SizeOf<WlanInterfaceInfo>();
            for (var i = 0; i < nombre; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(liste + 8 + i * taille);
                if (info.EtatInterface != EtatConnecte) continue;
                var guid = info.Guid;
                return LireInterface(handle, ref guid);
            }
            return null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            if (liste != IntPtr.Zero) WlanFreeMemory(liste);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static QualiteWifi? LireInterface(IntPtr handle, ref Guid guid)
    {
        IntPtr qualite = IntPtr.Zero, canal = IntPtr.Zero;
        try
        {
            // Un bloc plus court que la structure connue n'est pas lu (pas de lecture hors tampon).
            if (WlanQueryInterface(handle, ref guid, OpcodeQualiteTempsReel, IntPtr.Zero, out var taille, out qualite, IntPtr.Zero) != 0
                || qualite == IntPtr.Zero || taille < Marshal.SizeOf<QualiteTempsReel>())
                return null;
            uint? numero = WlanQueryInterface(handle, ref guid, OpcodeCanal, IntPtr.Zero, out _, out canal, IntPtr.Zero) == 0
                           && canal != IntPtr.Zero ? (uint)Marshal.ReadInt32(canal) : null;
            return Decoder(qualite, numero);
        }
        finally
        {
            if (qualite != IntPtr.Zero) WlanFreeMemory(qualite);
            if (canal != IntPtr.Zero) WlanFreeMemory(canal);
        }
    }

    /// <summary>Le bloc WLAN_REALTIME_CONNECTION_QUALITY tel que le pilote le rend ; le premier lien porte la fréquence et le
    /// RSSI (le seul hors MLO).</summary>
    internal static QualiteWifi Decoder(IntPtr qualite, uint? canal)
    {
        var tete = Marshal.PtrToStructure<QualiteTempsReel>(qualite);
        LienTempsReel? lien = tete.NombreDeLiens > 0
            ? Marshal.PtrToStructure<LienTempsReel>(qualite + (int)Marshal.OffsetOf<QualiteTempsReel>(nameof(QualiteTempsReel.PremierLien)))
            : null;
        return new QualiteWifi((int)tete.PhyType, (int)Math.Min(tete.QualiteLien, 100u),
            lien?.Rssi, lien is { } l && l.FrequenceCentrale > 0 ? (int)l.FrequenceCentrale : null,
            canal is { } c and > 0 ? (int)c : null);
    }

    // wlanapi.h (SDK 10.0.26100.0), WLAN_REALTIME_CONNECTION_QUALITY et WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO.
    [StructLayout(LayoutKind.Sequential)]
    internal struct QualiteTempsReel
    {
        public uint PhyType;
        public uint QualiteLien;
        public uint DebitReception;
        public uint DebitEmission;
        public int EstMlo;
        public uint NombreDeLiens;
        public LienTempsReel PremierLien;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LienTempsReel
    {
        public byte IdLien;
        public uint FrequenceCentrale;
        public uint LargeurDeBande;
        public int Rssi;
        public uint LongueurDesDebits;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] Debits;
    }

    // WLAN_INTERFACE_INFO : GUID, WCHAR[256], WLAN_INTERFACE_STATE.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid Guid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int EtatInterface;
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserve, out uint negociee, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserve);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserve, out IntPtr liste);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid guid, uint opcode,
        IntPtr reserve, out uint taille, out IntPtr donnees, IntPtr typeDeValeur);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memoire);
}
