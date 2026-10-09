using System.Runtime.InteropServices;
using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// 06/10/2026 (chantier B3) : la carte graphique est-elle branchée comme elle le peut ?
///
/// (a) Le lien PCIe, lu par Windows pour toute carte, sans pilote : <c>DEVPKEY_PciDevice_MaxLinkWidth</c> (ce que la carte
/// sait faire) et <c>CurrentLinkWidth</c> (ce qui est négocié avec l'emplacement), devpkey.h, propriétés 9 à 12 de
/// {3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62} ; relevé le 06/10 : RTX 4060 en x8 sur x8, PCIe 4.0. Une largeur négociée sous
/// le maximum de la carte = emplacement secondaire, rallonge, ou carte mal enfoncée. La génération, elle, peut baisser au
/// repos pour économiser : elle est dite, jamais jugée. Le coût en images dépend de la carte et n'est pas chiffré.
///
/// (b) L'écran branché sur la sortie de la carte mère alors qu'une carte dédiée est là : le jeu passe alors par la puce
/// intégrée, ou recopie chaque image d'une carte à l'autre. Sur un portable c'est le montage normal : rien n'y est dit.
/// </summary>
public static class BranchementDeLaCarte
{
    public sealed record Lien(string Carte, int LargeurMax, int Largeur, int GenerationMax, int Generation);

    // Jusqu'à 1 Go, une puce intégrée (Windows lui prête 128 Mo à 1 Go) ; au-dessus, une carte dédiée. Entre les deux : rien n'est dit.
    private const long Integree = 1L << 30;
    private const long Dediee = 2L << 30;
    private static readonly System.Text.RegularExpressions.Regex CarteDediee =
        new(@"GeForce|Quadro|Radeon RX|Radeon Pro|\bArc\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <param name="batterie">Revue T3+T4 (F-20) : sur un portable la puce est soudée, câblée par le constructeur
    /// (RTX 4080 Laptop en x8) ; la largeur y est dite, pas jugée. null = non lu : rien n'est dit.</param>
    public static IEnumerable<RepairFinding> JugerLien(Lien l, bool? batterie = false)
    {
        if (l.LargeurMax <= 0 || l.Largeur <= 0) yield break;
        var lu = $"x{l.Largeur}" + (l.Generation > 0 ? $", PCIe {l.Generation}.0" : "");
        // Revue (F-35) : batterie non lue ⇒ le lien est dit sans jugement, pas tu.
        if (batterie is null && l.Largeur < l.LargeurMax)
        {
            yield return new RepairFinding("GPU", $"lien PCIe de {l.Carte}", lu, $"x{l.LargeurMax}", RepairStatus.Unknown,
                $"la carte sait travailler en x{l.LargeurMax} et tourne en x{l.Largeur}. DrDXT n'a pas pu lire si ce PC est un "
                + "portable (carte soudée, câblage choisi par le constructeur) ou un fixe (emplacement, rallonge) : non jugé.");
            yield break;
        }
        if (batterie == true && l.Largeur < l.LargeurMax)
        {
            yield return new RepairFinding("GPU", $"lien PCIe de {l.Carte}", lu, $"x{l.LargeurMax}", RepairStatus.Observed,
                $"la carte sait travailler en x{l.LargeurMax} et tourne en x{l.Largeur}. Sur un portable, la carte est soudée "
                + "et le constructeur choisit ce câblage : c'est dit, pas un défaut.");
            yield break;
        }
        yield return l.Largeur < l.LargeurMax
            ? new RepairFinding("GPU", $"lien PCIe de {l.Carte}", lu, $"x{l.LargeurMax}", RepairStatus.Suspicious,
                $"ta carte sait travailler en x{l.LargeurMax} et tourne en x{l.Largeur}. Les causes habituelles : elle est dans un "
                + "emplacement secondaire (moins de lignes câblées), sur une rallonge, ou mal enfoncée. Le manuel de ta carte "
                + "mère dit quel emplacement est câblé en x16 ; éteins le PC avant de la déplacer.",
                RepairAction.Recommandation)
            : new RepairFinding("GPU", $"lien PCIe de {l.Carte}", lu, $"x{l.LargeurMax}", RepairStatus.ExpectedDefault,
                "la carte tourne à la largeur qu'elle sait faire. La génération PCIe peut baisser au repos pour économiser.");
    }

    /// <param name="adaptateursDesEcrans">Le nom de l'adaptateur de chaque écran actif (EnumDisplayDevices).</param>
    /// <param name="batterie">null = non lu : rien n'est dit.</param>
    public static IEnumerable<RepairFinding> JugerEcrans(IReadOnlyList<string> adaptateursDesEcrans, IReadOnlyList<GpuInfo> cartes, bool? batterie)
    {
        if (batterie is not false) yield break;
        var dediee = cartes.FirstOrDefault(c => c.VramBytes >= Dediee);
        if (dediee is null) yield break;
        // Revue T3+T4 (F-21) : une GT 710 ou une Quadro de 1 Go est une carte dédiée, pas la carte mère.
        var integrees = adaptateursDesEcrans
            .Where(a => cartes.Any(c => c.VramBytes > 0 && c.VramBytes <= Integree && !CarteDediee.IsMatch(c.Model)
                                        && c.Model.Equals(a, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (integrees.Count == 0) yield break;
        yield return new RepairFinding("GPU", "écran branché sur la carte mère", string.Join(", ", integrees), dediee.Model,
            RepairStatus.Suspicious,
            // F-22 : un 2e écran sur la carte mère ne dit rien du jeu sur l'écran principal : la phrase parle de cet écran.
            $"un écran est branché sur une sortie de la carte mère ({string.Join(", ", integrees)}) alors que ta carte "
            + $"{dediee.Model} est là : un jeu affiché sur cet écran passe par la puce intégrée, ou recopie chaque image "
            + "d'une carte à l'autre. Pour l'écran où tu joues, "
            + "branche le câble sur une sortie de la carte graphique (en général plus bas, à l'horizontale).",
            RepairAction.Recommandation);
    }

    // ── Lecture ─────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<Lien> LireLiens(IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT Name, PNPDeviceID FROM Win32_VideoController");
        if (!r.IsOk) return [];
        var liens = new List<Lien>();
        foreach (var l in r.Value!)
        {
            if (l.GetString("PNPDeviceID") is not { } id || !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            liens.Add(new Lien(l.GetString("Name") ?? id, Propriete(id, 12), Propriete(id, 10), Propriete(id, 11), Propriete(id, 9)));
        }
        return liens;
    }

    /// <summary>07/10/2026 (chantier D5) : le lien PCIe de chaque SSD NVMe, par les mêmes propriétés que la carte.
    /// Le contrôleur se reconnaît à son code de classe PCI (CompatibleID « PCI\CC_010802 », stockage NVMe), pas à son nom
    /// traduit (« Contrôleur NVM Express standard »).</summary>
    public static IReadOnlyList<Lien> LireLiensNvme(IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT Name, PNPDeviceID, CompatibleID FROM Win32_PnPEntity WHERE PNPClass = 'SCSIAdapter'");
        if (!r.IsOk) return [];
        var liens = new List<Lien>();
        foreach (var l in r.Value!)
        {
            if (l.GetString("PNPDeviceID") is not { } id || !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            var compatibles = l.Properties.GetValueOrDefault("CompatibleID") as string[] ?? [];
            if (!compatibles.Any(c => c.Contains("CC_010802", StringComparison.OrdinalIgnoreCase))) continue;
            liens.Add(new Lien(l.GetString("Name") ?? id, Propriete(id, 12), Propriete(id, 10), Propriete(id, 11), Propriete(id, 9)));
        }
        return liens;
    }

    /// <summary>D5 : un SSD NVMe câblé sur moins de lignes que les siennes (x2 au lieu de x4) perd la moitié de son débit :
    /// un emplacement M.2 partagé ou relié au chipset. La génération sous son maximum est dite sans jugement (emplacement
    /// plus ancien, ou état d'économie). Le temps de chargement gagné n'est pas chiffré.</summary>
    public static IEnumerable<RepairFinding> JugerLienSsd(Lien l)
    {
        if (l.LargeurMax <= 0 || l.Largeur <= 0) yield break;
        var lu = $"x{l.Largeur}" + (l.Generation > 0 ? $", PCIe {l.Generation}.0" : "");
        var max = $"x{l.LargeurMax}" + (l.GenerationMax > 0 ? $", PCIe {l.GenerationMax}.0" : "");
        if (l.Largeur < l.LargeurMax)
            yield return new RepairFinding("STOCKAGE", $"lien PCIe du SSD {l.Carte}", lu, max, RepairStatus.Suspicious,
                $"ton SSD sait travailler en x{l.LargeurMax} et tourne en x{l.Largeur} : son débit maximal est divisé d'autant. "
                + "Les causes habituelles : un emplacement M.2 qui partage ses lignes avec un autre (carte graphique, port SATA), "
                + "ou relié au chipset. Le manuel de ta carte mère dit quel emplacement M.2 a toutes ses lignes ; éteins le PC "
                + "avant de déplacer le SSD.", RepairAction.Recommandation);
        else if (l.GenerationMax > 0 && l.Generation > 0 && l.Generation < l.GenerationMax)
            yield return new RepairFinding("STOCKAGE", $"lien PCIe du SSD {l.Carte}", lu, max, RepairStatus.Observed,
                $"ton SSD sait travailler en PCIe {l.GenerationMax}.0 et tourne en PCIe {l.Generation}.0 : l'emplacement M.2 est "
                + "peut-être d'une génération plus ancienne, ou le lien était en économie au moment de la lecture. Dit, pas jugé.");
        else
            yield return new RepairFinding("STOCKAGE", $"lien PCIe du SSD {l.Carte}", lu, max, RepairStatus.ExpectedDefault,
                "ton SSD tourne à la largeur et à la génération qu'il sait faire.");
    }

    private static readonly Guid FmtPci = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");

    // 0 = non lu.
    private static int Propriete(string instance, uint pid)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var noeud, instance, 0) != 0) return 0;
            var cle = new DevPropKey { Fmtid = FmtPci, Pid = pid };
            var tampon = new byte[4];
            uint taille = 4;
            return CM_Get_DevNode_PropertyW(noeud, ref cle, out _, tampon, ref taille, 0) == 0 && taille == 4
                ? (int)BitConverter.ToUInt32(tampon, 0) : 0;
        }
        catch (Exception) { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType,
        byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
}
