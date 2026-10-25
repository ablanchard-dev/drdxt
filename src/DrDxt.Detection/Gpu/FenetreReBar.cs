using System.Runtime.InteropServices;
using DrDxt.Core;
using Microsoft.Win32;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Le Resizable BAR prouvé par ce que Windows a vraiment alloué à chaque carte graphique, pas par la case du BIOS : la plus
/// grande plage mémoire de la carte, comparée à sa mémoire vidéo. Une plage au-dessus de 4 Gio prouve aussi « Above 4G
/// Decoding ». Chaque carte est lue par son propre identifiant PnP, sans passer par NVML ni par « la carte 0 ». Toutes
/// marques. Lecture seule, sans droits.
/// </summary>
/// <remarks>
/// cfgmgr32.h (Windows SDK 10.0.26100.0) : CM_Get_First_Log_Conf avec ALLOC_LOG_CONF (0x2), puis CM_Get_Next_Res_Des sur
/// ResType_Mem (0x1) et ResType_MemLarge (0x7, « Memory resources >= 4GB ») ; MEM_DES et MEM_LARGE_DES portent Count (4),
/// Type (4), Alloc_Base (8), Alloc_End (8). La mémoire vidéo : la valeur HardwareInformation.qwMemorySize que le pilote
/// d'affichage écrit sous sa clé de classe (Microsoft Learn, « Registering hardware information »). Relevé sur la machine
/// de référence (relevé du 16/10/2026) : RTX 4060, plage de 8192 Mio à 0x4000000000 pour 8 585 740 288 octets de mémoire vidéo.
/// </remarks>
public static class FenetreReBar
{
    /// <param name="PlageMax">La plus grande plage allouée, en octets ; null si aucune n'a été lue.</param>
    /// <param name="BaseMax">Où commence cette plage.</param>
    public sealed record Carte(string Nom, ulong? PlageMax, ulong BaseMax, ulong? MemoireVideo);

    /// <summary>L'ouverture classique d'une carte sans Resizable BAR : 256 Mio.</summary>
    public const ulong Classique = 256UL << 20;

    /// <summary>Une plage d'au moins la mémoire vidéo moins 1/16 la couvre entière. **Non chiffré** : l'écart entre la
    /// puissance de deux allouée et la mémoire déclarée par le pilote (8192 contre 8188 Mio ici) reste bien en dessous.</summary>
    public static bool Couvre(ulong plage, ulong memoire) => plage >= memoire - memoire / 16;

    public static RepairFinding Juger(Carte c)
    {
        var nom = Docteur.Nom("RB.Nom", c.Nom);
        if (c.PlageMax is not { } plage)
            return new RepairFinding("GPU", nom, null, null, RepairStatus.Unknown, Docteur.T("RB.NonLu"));
        var mio = plage >> 20;
        var au4G = c.BaseMax >= 1UL << 32 ? Docteur.T("RB.Au4G") : "";
        if (c.MemoireVideo is not { } vram || vram == 0)
            return new RepairFinding("GPU", nom, Docteur.T("RB.Valeur", mio), null, RepairStatus.Unknown, Docteur.T("RB.SansVram", mio) + au4G);
        // Une puce intégrée déclare une petite mémoire « dédiée » prise sur celle du système (128 Mio sur une Intel UHD) : toute
        // ouverture la couvre, ReBAR ou pas. Sous l'ouverture classique, la comparaison ne prouve rien.
        if (vram <= Classique)
            return new RepairFinding("GPU", nom, Docteur.T("RB.Valeur", mio), null, RepairStatus.Unknown,
                Docteur.T("RB.PetiteMemoire", mio, vram >> 20) + au4G);
        if (Couvre(plage, vram))
            return new RepairFinding("GPU", nom, Docteur.T("RB.Valeur", mio), null, RepairStatus.ExpectedDefault,
                Docteur.T("RB.Actif", mio, vram >> 20) + au4G);
        return plage <= Classique
            ? new RepairFinding("GPU", nom, Docteur.T("RB.Valeur", mio), Docteur.T("RB.Valeur", vram >> 20), RepairStatus.Observed,
                Docteur.T("RB.Inactif", mio, vram >> 20) + au4G)
            : new RepairFinding("GPU", nom, Docteur.T("RB.Valeur", mio), Docteur.T("RB.Valeur", vram >> 20), RepairStatus.Observed,
                Docteur.T("RB.Partiel", mio, vram >> 20) + au4G);
    }

    /// <summary>Le vrai : chaque carte de Win32_VideoController, sa plage allouée et sa mémoire vidéo.</summary>
    public static IEnumerable<RepairFinding> LireEtJuger(Wmi.IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT Name, PNPDeviceID FROM Win32_VideoController");
        // Les cartes non listées : un constat inconnu, pas une famille muette.
        if (!r.IsOk)
            return [new RepairFinding("GPU", "Resizable BAR", null, null, RepairStatus.Unknown, Docteur.T("RB.CartesNonLues"))];
        return r.Value!.Where(l => l.GetString("PNPDeviceID")?.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) == true)
            .Select(l => Juger(Lire(l.GetString("Name") ?? "?", l.GetString("PNPDeviceID")!))).ToList();
    }

    internal static Carte Lire(string nom, string instance)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var noeud, instance, 0) != 0) return new(nom, null, 0, null);
            ulong? plageMax = null;
            ulong baseMax = 0;
            if (CM_Get_First_Log_Conf(out var conf, noeud, 0x2) == 0)
            {
                try
                {
                    foreach (var type in new uint[] { 0x1, 0x7 })
                    {
                        var courant = conf;
                        while (CM_Get_Next_Res_Des(out var rd, courant, type, out _, 0) == 0)
                        {
                            if (courant != conf) CM_Free_Res_Des_Handle(courant);
                            courant = rd;
                            if (CM_Get_Res_Des_Data_Size(out var taille, rd, 0) != 0 || taille < 24) continue;
                            var donnees = new byte[taille];
                            if (CM_Get_Res_Des_Data(rd, donnees, taille, 0) != 0) continue;
                            var debut = BitConverter.ToUInt64(donnees, 8);
                            var fin = BitConverter.ToUInt64(donnees, 16);
                            if (fin < debut) continue;
                            var plage = fin - debut + 1;
                            if (plage > (plageMax ?? 0)) (plageMax, baseMax) = (plage, debut);
                        }
                        if (courant != conf) CM_Free_Res_Des_Handle(courant);
                    }
                }
                finally { CM_Free_Log_Conf_Handle(conf); }
            }
            return new(nom, plageMax, baseMax, MemoireVideo(noeud));
        }
        catch (Exception) { return new(nom, null, 0, null); }
    }

    private static ulong? MemoireVideo(uint noeud)
    {
        var k = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 11 };   // DEVPKEY_Device_Driver (devpkey.h)
        var tampon = new byte[512];
        uint taille = 512;
        if (CM_Get_DevNode_PropertyW(noeud, ref k, out _, tampon, ref taille, 0) != 0 || taille < 4) return null;
        var cle = System.Text.Encoding.Unicode.GetString(tampon, 0, (int)taille).TrimEnd('\0');
        using var classe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + cle);
        return classe?.GetValue("HardwareInformation.qwMemorySize") switch
        {
            long l when l > 0 => (ulong)l,
            byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
            _ => null,
        };
    }

    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_First_Log_Conf(out nint plcLogConf, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Next_Res_Des(out nint prdResDes, nint rdResDes, uint ForResource, out uint pResourceID, uint ulFlags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Res_Des_Data_Size(out uint pulSize, nint rdResDes, uint ulFlags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Res_Des_Data(nint rdResDes, byte[] Buffer, uint BufferLen, uint ulFlags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Free_Res_Des_Handle(nint rdResDes);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Free_Log_Conf_Handle(nint lcLogConf);
}
