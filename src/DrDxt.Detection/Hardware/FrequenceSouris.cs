using System.Runtime.InteropServices;
using DrDxt.Core;
using DrDxt.Detection.Wmi;
using Microsoft.Win32.SafeHandles;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// La fréquence d'interrogation qu'une souris USB annonce. Lue sans pilote ni
/// mouvement de souris : le hub USB rend les tuyaux ouverts de l'appareil (IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
/// usbioctl.h) et le point d'accès d'interruption entrant porte bInterval. USB 2.0 §9.6.6 : en basse et pleine vitesse,
/// bInterval est en millisecondes (1 à 255) ; en haute vitesse et au-delà, l'intervalle vaut 2^(bInterval−1) × 125 µs
/// (1 à 16). C'est la fréquence maximale que la souris annonce : un logiciel de souris peut la régler plus bas sans changer
/// le descripteur. Au-dessous de 125 Hz le défaut est certain ; au-dessus, on dit « jusqu'à ». Une souris Bluetooth ou
/// derrière un récepteur partagé peut rester non lue.
/// </summary>
public static class FrequenceSouris
{
    /// <param name="vitesse">USB_DEVICE_SPEED : 0 basse, 1 pleine, 2 haute, 3 super.</param>
    // En basse et pleine vitesse, le contrôleur arrondit l'intervalle à la puissance de 2
    // inférieure (xHCI 1.2 §6.2.3.6 ; EHCI pareil) : 9 ou 10 ms annoncées sont servies toutes les 8 ms.
    public static int? Hz(int vitesse, int intervalle) => vitesse switch
    {
        0 or 1 when intervalle is >= 1 and <= 255 => 1000 / (1 << System.Numerics.BitOperations.Log2((uint)intervalle)),
        2 or 3 when intervalle is >= 1 and <= 16 => 8000 >> (intervalle - 1),
        _ => null,
    };

    // USB_NODE_CONNECTION_INFORMATION_EX (empaqueté sur 1 octet) : vitesse à l'octet 23, nombre de tuyaux ouverts aux
    // octets 27-30, puis à 35 les USB_PIPE_INFO de 11 octets (descripteur de point d'accès de 7 octets + ScheduleOffset).
    /// <summary>La vitesse du bus et le plus petit intervalle des points d'accès d'interruption entrants ; null sans eux.
    /// Sur un appareil composite (souris + clavier sur un récepteur), le plus petit peut être
    /// celui du clavier ; des intervalles différents y rendent null (non lu).</summary>
    public static (int Vitesse, int Intervalle)? Lire(byte[] info, bool composite)
    {
        if (info.Length < 35) return null;
        var tuyaux = BitConverter.ToInt32(info, 27);
        var intervalles = new List<int>();
        for (var i = 0; i < tuyaux && 35 + (i + 1) * 11 <= info.Length; i++)
        {
            var p = 35 + i * 11;
            // bEndpointAddress bit 7 = entrant ; bmAttributes & 3 = 3 = interruption ; bInterval à l'octet 6.
            if ((info[p + 2] & 0x80) != 0 && (info[p + 3] & 3) == 3 && info[p + 6] > 0) intervalles.Add(info[p + 6]);
        }
        if (intervalles.Count == 0 || (composite && intervalles.Distinct().Count() > 1)) return null;
        return (info[23], intervalles.Min());
    }

    /// <summary>HID sur Bluetooth classique (UUID 0x1124) ou basse énergie (HOGP, 0x1812).</summary>
    public static bool EstBluetooth(string idHid)
        => idHid.Contains("{00001124-", StringComparison.OrdinalIgnoreCase) || idHid.Contains("{00001812-", StringComparison.OrdinalIgnoreCase);

    public static RepairFinding Juger(string nom, int? hz) => hz switch
    {
        null => new RepairFinding("LATENCE", Docteur.Nom("FS.Nom", nom), null, null, RepairStatus.Unknown,
            Docteur.T("FS.NonLue")),
        <= 125 => new RepairFinding("LATENCE", Docteur.Nom("FS.Nom", nom), Docteur.T("FS.Hz", hz), Docteur.T("FS.Hz", 1000), RepairStatus.Suspicious,
            Docteur.T("FS.Lente", hz, 1000 / hz),
            RepairAction.Recommandation),
        _ => new RepairFinding("LATENCE", Docteur.Nom("FS.Nom", nom), Docteur.T("FS.JusquA", hz), null, RepairStatus.Observed,
            Docteur.T("FS.Max")),
    };

    // ── Lecture ─────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<RepairFinding> LireEtJuger(IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT Name, PNPDeviceID FROM Win32_PointingDevice");
        if (!r.IsOk) return [];
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constats = new List<RepairFinding>();
        foreach (var l in r.Value!)
        {
            // WMI rend l'appareil USB (relevé du 06/10 : « USB\VID_0738&PID_1703\… ») ou le périphérique HID.
            if (l.GetString("PNPDeviceID") is not { } id
                || !(id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))) continue;
            var usb = PremierParentUsb(id);
            // Un pavé tactile (I2C, PS/2) n'est ni USB ni Bluetooth : rien n'est dit.
            if (usb is null && !EstBluetooth(id)) continue;
            if (usb is not null && !vus.Add(usb.Value.Instance)) continue;
            var nom = (usb is { } u ? Texte(u.Noeud, ProprieteNom) : null) ?? l.GetString("Name") ?? "souris";
            constats.Add(Juger(nom, usb is { } v ? LireHz(v.Noeud, v.Composite) : null));
        }
        return constats;
    }

    private static int? LireHz(uint noeud, bool composite)
    {
        try
        {
            if (Entier(noeud, ProprieteAdresse) is not { } port || CM_Get_Parent(out var hub, noeud, 0) != 0) return null;
            if (InstanceDe(hub) is not { } instanceHub || CheminDuHub(instanceHub) is not { } chemin) return null;
            using var h = CreateFileW(chemin, 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var info = new byte[35 + 32 * 11];
            BitConverter.GetBytes(port).CopyTo(info, 0);
            if (!DeviceIoControl(h, 0x220448, info, info.Length, info, info.Length, out _, IntPtr.Zero)) return null;
            return Lire(info, composite) is var (vitesse, intervalle) ? Hz(vitesse, intervalle) : null;
        }
        catch (Exception) { return null; }
    }

    // Le premier ancêtre USB qui est l'appareil lui-même (pas une interface « &MI_ » d'un appareil composite).
    // Composite = le chemin est passé par une interface « &MI_ » : l'appareil porte d'autres fonctions (clavier…).
    private static (uint Noeud, string Instance, bool Composite)? PremierParentUsb(string idHid)
    {
        if (CM_Locate_DevNodeW(out var n, idHid, 0) != 0) return null;
        if (InstanceDe(n) is { } soi && soi.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            && !soi.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            return (n, soi, false);
        var composite = false;
        for (var i = 0; i < 4 && CM_Get_Parent(out var p, n, 0) == 0; i++, n = p)
            if (InstanceDe(p) is { } id && id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                if (!id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return (p, id, composite);
                composite = true;
            }
        return null;
    }

    private static readonly Guid InterfaceHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private static readonly (Guid, uint) ProprieteAdresse = (new("a45c254e-df1c-4efd-8020-67d146a850e0"), 30);
    private static readonly (Guid, uint) ProprieteNom = (new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    private static string? CheminDuHub(string instance)
    {
        var g = InterfaceHub;
        if (CM_Get_Device_Interface_List_SizeW(out var taille, ref g, instance, 0) != 0 || taille <= 1) return null;
        var tampon = new char[taille];
        return CM_Get_Device_Interface_ListW(ref g, instance, tampon, taille, 0) == 0
            ? new string(tampon).Split('\0')[0] is { Length: > 0 } c ? c : null : null;
    }

    private static string? InstanceDe(uint noeud)
    {
        var tampon = new char[512];
        return CM_Get_Device_IDW(noeud, tampon, tampon.Length, 0) == 0 ? new string(tampon).TrimEnd('\0') : null;
    }

    private static int? Entier(uint noeud, (Guid Fmt, uint Pid) cle)
    {
        var k = new DevPropKey { Fmtid = cle.Fmt, Pid = cle.Pid };
        var tampon = new byte[4];
        uint taille = 4;
        return CM_Get_DevNode_PropertyW(noeud, ref k, out _, tampon, ref taille, 0) == 0 && taille == 4 ? BitConverter.ToInt32(tampon, 0) : null;
    }

    private static string? Texte(uint noeud, (Guid Fmt, uint Pid) cle)
    {
        var k = new DevPropKey { Fmtid = cle.Fmt, Pid = cle.Pid };
        var tampon = new byte[512];
        uint taille = 512;
        return CM_Get_DevNode_PropertyW(noeud, ref k, out _, tampon, ref taille, 0) == 0 && taille > 2
            ? System.Text.Encoding.Unicode.GetString(tampon, 0, (int)taille).TrimEnd('\0') : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, char[] buffer, int bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType,
        byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid interfaceClassGuid, string deviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string deviceID, char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer,
        int outSize, out int returned, IntPtr overlapped);
}
