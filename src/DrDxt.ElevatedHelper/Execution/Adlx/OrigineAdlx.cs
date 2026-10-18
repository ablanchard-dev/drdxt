using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

/// <summary>
/// L'origine de la MaxFreq d'une carte AMD, gardée d'un démarrage à l'autre. Retenue seulement par démarrage, elle suivait
/// la valeur que DrDXT avait posée si le pilote la garde au redémarrage : +100 MHz par démarrage, jusqu'au plafond
/// d'overclock du pilote. Chaque carte (identifiée par ses identifiants PNP) garde dans
/// <c>HKLM\SOFTWARE\DrDXT\Helper\AdlxOrigine</c> son origine et la dernière valeur que DrDXT y a écrite. Au premier passage
/// d'un démarrage, une MaxFreq lue égale à cette dernière valeur garde l'origine ; une autre (le joueur l'a réglée dans AMD
/// Software, ou le pilote l'a remise à l'usine) devient la nouvelle origine. La clé ne s'écrit qu'en administrateur, et la
/// désinstallation retire <c>HKLM\SOFTWARE\DrDXT</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OrigineAdlx(Func<int, string?> identite, RegistryKey? racine = null, string? chemin = null)
{
    private const string CleParDefaut = @"SOFTWARE\DrDXT\Helper";
    private RegistryKey Racine => racine ?? Registry.LocalMachine;
    private string Chemin => chemin ?? CleParDefaut;

    public static OrigineAdlx ParDefaut() => new(IdentiteParWmi);

    /// <summary>L'origine de ce démarrage, retenue au premier passage ; null si la carte ou la mémoire ne se lisent pas
    /// (rien n'est alors écrit).</summary>
    public int? Retenir(int gpu, int lue)
    {
        if (identite(gpu) is not { } id) return null;
        var candidate = Lire(id) is { } p && p.Ecrite == lue ? p.Origine : lue;
        return OrigineDuDemarrage.Retenir("AdlxMaxFreq" + gpu, unchecked((ulong)candidate), racine, chemin) is { } o
            ? unchecked((int)o)
            : null;
    }

    /// <summary>Notée avant chaque écriture (et après un retour à la valeur d'avant) : faux si elle ne s'écrit pas, et rien
    /// n'est alors écrit sur la carte.</summary>
    public bool Noter(int gpu, int origine, int ecrite)
    {
        if (identite(gpu) is not { } id) return false;
        try
        {
            using var cle = Racine.CreateSubKey(Chemin + @"\AdlxOrigine", writable: true);
            cle.SetValue(id, ((long)origine << 32) | (uint)ecrite, RegistryValueKind.QWord);
            return true;
        }
        catch (Exception) { return false; }
    }

    private (int Origine, int Ecrite)? Lire(string id)
    {
        try
        {
            using var cle = Racine.OpenSubKey(Chemin + @"\AdlxOrigine");
            return cle?.GetValue(id) is long v ? ((int)(v >> 32), unchecked((int)(v & 0xFFFFFFFF))) : null;
        }
        catch (Exception) { return null; }
    }

    // Les cartes AMD de la machine, triées, et l'index ADLX : une carte ajoutée ou retirée change l'identité, et l'origine
    // repart de la valeur lue. Plus fin (l'identifiant PNP rendu par ADLX pour chaque index) demanderait une entrée de vtable
    // de plus, non vérifiée sur matériel.
    private static string? IdentiteParWmi(int gpu)
    {
        try
        {
            var ids = new List<string>();
            using var s = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT PNPDeviceID FROM Win32_VideoController");
            using var rangees = s.Get();
            foreach (var o in rangees)
                using (o)
                    if (o["PNPDeviceID"] is string pnp && pnp.StartsWith(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase))
                        ids.Add(pnp);
            return ids.Count == 0 ? null : gpu + "|" + string.Join(";", ids.Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception) { return null; }
    }
}
