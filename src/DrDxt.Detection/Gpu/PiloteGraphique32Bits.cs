using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Le pilote graphique en mode utilisateur 32 bits de chaque vraie carte : sans lui, une application 32 bits (un lanceur
/// de jeu, par exemple) ne peut pas ouvrir la carte et reste à l'écran noir. Lecture seule ; les cartes virtuelles
/// (affichage à distance, pilote de base) ne sont pas lues.
/// </summary>
/// <remarks>
/// Microsoft Learn, « Windows Display Driver Model (WDDM) 64-Bit Issues » : pour qu'une application 32 bits tourne sur un
/// Windows 64 bits, un pilote mode utilisateur 32 bits doit être fourni en plus du 64 bits, et son nom est inscrit sous
/// UserModeDriverNameWow (REG_MULTI_SZ) dans la clé du pilote. Relevé sur la machine de référence le 17/10/2026 :
/// UserModeDriverName et UserModeDriverNameWow présents, chacun vers un fichier du magasin de pilotes. La page donne
/// aussi la forme d'un simple nom de fichier (Contoso.dll) : un nom sans dossier se cherche dans le dossier système
/// 32 bits (SysWOW64), pas dans le dossier courant de DrDXT.
/// </remarks>
public static class PiloteGraphique32Bits
{
    public sealed record Carte(string Nom, IReadOnlyList<string>? Noms64, IReadOnlyList<string>? Noms32);

    /// <summary>Pur, avec un test d'existence injecté : null = rien à redire ; sinon la clé du texte et les fichiers
    /// manquants.</summary>
    public static (string Cle, string Manquants)? Defaut(Carte c, Func<string, bool> existe)
    {
        if (c.Noms64 is null || c.Noms64.Count == 0) return null;   // pas un pilote WDDM lu : rien n'est dit
        if (c.Noms32 is null || c.Noms32.Count == 0) return ("G32.Absent", "");
        var manquants = c.Noms32.Where(n => !existe(Chemin32(n))).ToList();
        return manquants.Count > 0 ? ("G32.Manquant", string.Join(", ", manquants.Select(Path.GetFileName))) : null;
    }

    /// <summary>Pur : un chemin complet reste tel quel, un simple nom de fichier se lit dans SysWOW64.</summary>
    public static string Chemin32(string nom)
        => Path.IsPathRooted(nom) ? nom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), nom);

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Carte>? cartes, Func<string, bool> existe)
    {
        if (cartes is null)
        {
            yield return new RepairFinding("GPU DRIVER", "pilote graphique 32 bits", null, null, RepairStatus.Unknown, Docteur.T("G32.NonLu"));
            yield break;
        }
        foreach (var c in cartes)
            // Rien de lu (pilote de base, clé de classe introuvable ou illisible) n'est pas « ses fichiers sont là ».
            yield return c.Noms64 is not { Count: > 0 }
                ? new RepairFinding("GPU DRIVER", Docteur.Nom("G32.Nom", c.Nom), null, null, RepairStatus.Unknown, Docteur.T("G32.CarteNonLue", c.Nom))
                : Defaut(c, existe) is { } d
                ? new RepairFinding("GPU DRIVER", Docteur.Nom("G32.Nom", c.Nom), Docteur.T("G32.ValeurAbsent"), null, RepairStatus.Suspicious,
                    Docteur.T(d.Cle, c.Nom, d.Manquants), RepairAction.Recommandation)
                : new RepairFinding("GPU DRIVER", Docteur.Nom("G32.Nom", c.Nom), Docteur.T("G32.ValeurPresent"), null, RepairStatus.ExpectedDefault,
                    Docteur.T("G32.Present"));
    }

    /// <summary>Les vraies cartes (PCI) de Win32_VideoController et les noms de leurs pilotes mode utilisateur.</summary>
    public static IReadOnlyList<Carte>? Lire(Wmi.IWmiQuery wmi)
    {
        var r = wmi.Query("SELECT Name, PNPDeviceID FROM Win32_VideoController");
        if (!r.IsOk) return null;
        var cartes = new List<Carte>();
        foreach (var l in r.Value!)
        {
            if (l.GetString("PNPDeviceID") is not { } id || !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            var cle = Hardware.ArbreDesPeripheriques.Texte(id, new Hardware.ArbreDesPeripheriques.Cle(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 11));
            if (cle is null) { cartes.Add(new Carte(l.GetString("Name") ?? id, null, null)); continue; }
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + cle);
                cartes.Add(new Carte(l.GetString("Name") ?? id, k?.GetValue("UserModeDriverName") as string[], k?.GetValue("UserModeDriverNameWow") as string[]));
            }
            catch (Exception) { cartes.Add(new Carte(l.GetString("Name") ?? id, null, null)); }
        }
        return cartes;
    }
}
