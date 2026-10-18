using System.Runtime.InteropServices;
using System.Text;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// L'arbre des périphériques de Windows, lu par le gestionnaire de configuration (cfgmgr32) : les ancêtres d'un appareil
/// jusqu'à la racine, et ses propriétés. Sert aux périphériques d'entrée (couches logicielles, liaison, réveil). Lecture
/// seule, sans droits. Les clés de propriétés viennent de devpkey.h (Windows SDK 10.0.26100.0).
/// </summary>
public static class ArbreDesPeripheriques
{
    public readonly record struct Cle(Guid Format, uint Numero);

    private static readonly Guid Appareil = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public static readonly Cle Service = new(Appareil, 6);
    public static readonly Cle FiltresHauts = new(Appareil, 19);
    public static readonly Cle FiltresBas = new(Appareil, 20);
    public static readonly Cle DonneesAlimentation = new(Appareil, 32);
    public static readonly Cle NomConvivial = new(Appareil, 14);
    public static readonly Cle Description = new(Appareil, 2);

    /// <summary>L'appareil puis ses ancêtres, identifiants d'instance, jusqu'à la racine ; vide s'il n'est pas trouvé.</summary>
    public static IReadOnlyList<string> Chaine(string instance, int profondeurMax = 16)
    {
        var chaine = new List<string>();
        try
        {
            if (CM_Locate_DevNodeW(out var noeud, instance, 0) != 0) return chaine;
            for (var i = 0; i < profondeurMax; i++)
            {
                if (IdentifiantDInstance(noeud) is { } id) chaine.Add(id);
                if (CM_Get_Parent(out var parent, noeud, 0) != 0) break;
                noeud = parent;
            }
        }
        catch (Exception) { /* la chaîne s'arrête au dernier parent lu */ }
        return chaine;
    }

    public static byte[]? Binaire(string instance, Cle cle)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var noeud, instance, 0) != 0) return null;
            var k = new DEVPROPKEY { fmtid = cle.Format, pid = cle.Numero };
            uint taille = 0;
            CM_Get_DevNode_PropertyW(noeud, ref k, out _, null, ref taille, 0);
            if (taille == 0) return null;
            var tampon = new byte[taille];
            return CM_Get_DevNode_PropertyW(noeud, ref k, out _, tampon, ref taille, 0) == 0 ? tampon[..(int)taille] : null;
        }
        catch (Exception) { return null; }
    }

    public static string? Texte(string instance, Cle cle)
        => Binaire(instance, cle) is { Length: >= 2 } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;

    /// <summary>Une liste de chaînes (DEVPROP_TYPE_STRING_LIST : chaînes terminées par zéro, puis un zéro final).</summary>
    public static IReadOnlyList<string> Liste(string instance, Cle cle)
        => Binaire(instance, cle) is { } b
            ? Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : [];

    // L'identifiant d'instance d'un nœud (CM_Get_Device_IDW) ; null s'il ne se lit pas.
    private static string? IdentifiantDInstance(uint dnDevInst)
    {
        var tampon = new char[512];
        return CM_Get_Device_IDW(dnDevInst, tampon, tampon.Length, 0) == 0 ? new string(tampon).TrimEnd('\0') : null;
    }

}
