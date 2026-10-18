using System.Runtime.InteropServices;
using System.Text;
using DrDxt.Core;
using Microsoft.Win32.SafeHandles;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// L'inventaire des périphériques d'entrée : pour chaque appareil, le rôle qu'il déclare (souris, clavier, manette) et
/// comment il est relié (USB direct ou derrière des concentrateurs, Bluetooth). Le rôle vient de la page et de l'usage de
/// ses collections HID, jamais du fabricant : un VID ne dit pas ce qu'est l'appareil. Lecture seule, sans droits.
/// </summary>
/// <remarks>
/// Les interfaces HID présentes (GUID_DEVINTERFACE_HID, hidclass.h), ouvertes sans droit d'accès (CreateFile, accès 0) ;
/// HidD_GetAttributes, HidD_GetProductString, HidD_GetPreparsedData puis HidP_GetCaps (Microsoft Learn, « Opening HID
/// collections ») ; usages de hidusage.h (Windows SDK 10.0.26100.0) : page 0x01, souris 0x02, manette de jeu 0x04 et
/// 0x05, clavier 0x06, contrôleur multi-axes 0x08 ; page 0x0C, commandes multimédia.
/// </remarks>
public static class PeripheriquesDEntree
{
    public enum Role { Souris, Clavier, Manette, Multimedia, Autre }

    public enum Bus { Usb, Bluetooth, Autre }

    /// <param name="Noeud">Le nœud de l'appareil physique (USB ou Bluetooth), qui regroupe ses collections.</param>
    /// <param name="Concentrateurs">Le nombre de concentrateurs USB entre l'appareil et le port de la carte.</param>
    public sealed record Appareil(string Nom, string Noeud, IReadOnlyList<Role> Roles, Bus Liaison, int Concentrateurs, ushort Vid, ushort Pid);

    /// <summary>Pur : le rôle d'une collection HID par sa page et son usage.</summary>
    public static Role RoleDe(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x02) => Role.Souris,
        (0x01, 0x06) => Role.Clavier,
        (0x01, 0x04 or 0x05 or 0x08) => Role.Manette,
        (0x0C, _) => Role.Multimedia,
        _ => Role.Autre,
    };

    /// <summary>Pur : le nœud physique d'une collection (premier ancêtre USB qui n'est pas une interface « &amp;MI_ », ou le
    /// nœud Bluetooth), sa liaison et les concentrateurs traversés jusqu'au port de la carte.</summary>
    public static (string Noeud, Bus Liaison, int Concentrateurs)? Liaison(IReadOnlyList<string> chaine, Func<string, string?> service)
    {
        var bt = chaine.FirstOrDefault(c => c.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
                                             || c.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase));
        if (bt is not null) return (bt, Bus.Bluetooth, 0);
        var i = chaine.ToList().FindIndex(c => c.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                                               && !c.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return chaine.Count > 0 ? (chaine[0], Bus.Autre, 0) : null;
        var concentrateurs = chaine.Skip(i + 1).TakeWhile(c => !c.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
            .Count(c => service(c) is { } s && Concentrateurs.Contains(s));
        return (chaine[i], Bus.Usb, concentrateurs);
    }

    // Les services des concentrateurs USB de Windows ; un nom de service se compare sans la casse.
    private static readonly HashSet<string> Concentrateurs = new(StringComparer.OrdinalIgnoreCase) { "USBHUB3", "usbhub" };

    public static RepairFinding Juger(IReadOnlyList<Appareil>? appareils)
    {
        if (appareils is null)
            return new RepairFinding("PERIPHERIQUES", "appareils d'entree", null, null, RepairStatus.Unknown, Docteur.T("PE.NonLu"));
        var jeu = appareils.Where(a => a.Roles.Any(r => r is Role.Souris or Role.Clavier or Role.Manette)).ToList();
        if (jeu.Count == 0)
            return new RepairFinding("PERIPHERIQUES", "appareils d'entree", "0", null, RepairStatus.Observed, Docteur.T("PE.Aucun"));
        var lignes = jeu.Select(a => Docteur.T("PE.Ligne", a.Nom,
            string.Join(" + ", a.Roles.Where(r => r is not Role.Autre).Distinct().Select(r => Docteur.T("PE.Role." + r))),
            a.Liaison switch
            {
                Bus.Bluetooth => Docteur.T("PE.Bluetooth"),
                Bus.Usb => a.Concentrateurs > 0 ? Docteur.T("PE.UsbConcentrateur", a.Concentrateurs) : Docteur.T("PE.UsbDirect"),
                _ => Docteur.T("PE.AutreBus"),
            }));
        return new RepairFinding("PERIPHERIQUES", "appareils d'entree", $"{jeu.Count}", null, RepairStatus.Observed,
            string.Join(" ", lignes) + Docteur.T("PE.Declare"));
    }

    // ── Lecture ─────────────────────────────────────────────────────────────────────────

    private static readonly Guid InterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public static IReadOnlyList<Appareil>? Lire()
    {
        try
        {
            var g = InterfaceHid;
            if (CM_Get_Device_Interface_List_SizeW(out var taille, ref g, null, 0) != 0 || taille <= 1) return [];
            var tampon = new char[taille];
            if (CM_Get_Device_Interface_ListW(ref g, null, tampon, taille, 0) != 0) return null;
            var parNoeud = new Dictionary<string, (string Nom, List<Role> Roles, Bus Bus, int Hubs, ushort Vid, ushort Pid)>(StringComparer.OrdinalIgnoreCase);
            foreach (var chemin in new string(tampon).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Instance(chemin) is not { } instance) continue;
                var chaine = ArbreDesPeripheriques.Chaine(instance);
                if (Liaison(chaine, n => ArbreDesPeripheriques.Texte(n, ArbreDesPeripheriques.Service)) is not var (noeud, bus, hubs)) continue;
                var (page, usage, vid, pid, produit) = Collection(chemin);
                if (!parNoeud.TryGetValue(noeud, out var a))
                    a = (produit ?? ArbreDesPeripheriques.Texte(noeud, ArbreDesPeripheriques.Description) ?? noeud, [], bus, hubs, vid, pid);
                a.Roles.Add(RoleDe(page, usage));
                parNoeud[noeud] = a;
            }
            return parNoeud.Select(p => new Appareil(p.Value.Nom, p.Key, p.Value.Roles, p.Value.Bus, p.Value.Hubs, p.Value.Vid, p.Value.Pid)).ToList();
        }
        catch (Exception) { return null; }
    }

    private static (ushort Page, ushort Usage, ushort Vid, ushort Pid, string? Produit) Collection(string chemin)
    {
        using var h = CreateFileW(chemin, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return (0, 0, 0, 0, null);
        var attributs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        HidD_GetAttributes(h, ref attributs);
        var nom = new byte[256];
        var produit = HidD_GetProductString(h, nom, nom.Length) ? Encoding.Unicode.GetString(nom).Split('\0')[0] : null;
        ushort page = 0, usage = 0;
        if (HidD_GetPreparsedData(h, out var donnees))
        {
            try
            {
                var caps = new byte[64];
                if (HidP_GetCaps(donnees, caps) == 0x00110000) (usage, page) = (BitConverter.ToUInt16(caps, 0), BitConverter.ToUInt16(caps, 2));
            }
            finally { HidD_FreePreparsedData(donnees); }
        }
        return (page, usage, attributs.VendorID, attributs.ProductID, string.IsNullOrWhiteSpace(produit) ? null : produit.Trim());
    }

    private static string? Instance(string chemin)
    {
        var cle = new DEVPROPKEY { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };   // DEVPKEY_Device_InstanceId
        var tampon = new byte[1024];
        uint taille = (uint)tampon.Length;
        return CM_Get_Device_Interface_PropertyW(chemin, ref cle, out _, tampon, ref taille, 0) == 0
            ? Encoding.Unicode.GetString(tampon, 0, (int)taille).TrimEnd('\0') : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[] PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);
    // hidsdi.h : ces quatre fonctions rendent un BOOLEAN, un octet ; lu en BOOL (quatre octets), le résultat serait faux.
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, byte[] Buffer, int BufferLength);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
}
