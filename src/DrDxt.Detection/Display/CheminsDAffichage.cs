using System.Text;

namespace DrDxt.Detection.Display;

/// <summary>
/// Les chemins d'affichage actifs, tels que Windows les tient : une source (le bureau rendu par une carte) vers une sortie
/// (un écran). Deux sorties sur la même source = des écrans dupliqués. Lecture seule.
/// </summary>
/// <remarks>
/// Microsoft Learn : QueryDisplayConfig avec QDC_ONLY_ACTIVE_PATHS (0x2) rend les chemins actifs ; tailles prises par
/// GetDisplayConfigBufferSizes. DISPLAYCONFIG_PATH_INFO fait 72 octets : source (LUID, id, index de mode, état), puis cible
/// (LUID à 20, id à 28, technologie de sortie à 36, fréquence en fraction DISPLAYCONFIG_RATIONAL à 48 et 52). Le nom de
/// l'écran vient de DisplayConfigGetDeviceInfo avec DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME (2), paquet de 420 octets
/// dont monitorFriendlyDeviceName (64 caractères) à 36. Relevé sur la machine de référence le 17/10/2026 : deux chemins,
/// deux sources, 200 Hz et 144 Hz.
/// </remarks>
public static class CheminsDAffichage
{
    public sealed record Chemin(long SourceCarte, uint SourceId, long CibleCarte, uint CibleId, uint Sortie, double? Hz, string? Nom);

    internal const int TaillePath = 72;
    internal const int TailleMode = 64;
    private const uint QdcOnlyActivePaths = 0x2;
    private const int TailleNomCible = 420;

    /// <summary>Pur : découpe le tableau de chemins rendu par QueryDisplayConfig.</summary>
    public static IReadOnlyList<Chemin> Decouper(byte[] chemins, int nombre, Func<long, uint, string?>? nom = null)
    {
        var liste = new List<Chemin>(nombre);
        for (var i = 0; i < nombre && (i + 1) * TaillePath <= chemins.Length; i++)
        {
            var o = i * TaillePath;
            var num = BitConverter.ToUInt32(chemins, o + 48);
            var den = BitConverter.ToUInt32(chemins, o + 52);
            var cibleCarte = BitConverter.ToInt64(chemins, o + 20);
            var cibleId = BitConverter.ToUInt32(chemins, o + 28);
            liste.Add(new Chemin(BitConverter.ToInt64(chemins, o), BitConverter.ToUInt32(chemins, o + 8), cibleCarte, cibleId,
                BitConverter.ToUInt32(chemins, o + 36), den == 0 || num == 0 ? null : (double)num / den, nom?.Invoke(cibleCarte, cibleId)));
        }
        return liste;
    }

    /// <summary>Les chemins actifs ; null = Windows n'a pas répondu (session sans bureau, appel refusé).</summary>
    public static IReadOnlyList<Chemin>? Lire()
    {
        // ERROR_INSUFFICIENT_BUFFER (122) quand la configuration change entre les deux appels : on recommence, borné.
        for (var essai = 0; essai < 3; essai++)
        {
            if (Natif.GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var nChemins, out var nModes) != 0) return null;
            var chemins = new byte[nChemins * TaillePath];
            var modes = new byte[nModes * TailleMode];
            var rc = Natif.QueryDisplayConfig(QdcOnlyActivePaths, ref nChemins, chemins, ref nModes, modes, IntPtr.Zero);
            if (rc == 122) continue;
            return rc == 0 ? Decouper(chemins, (int)nChemins, NomDeLaCible) : null;
        }
        return null;
    }

    /// <summary>Un paquet DisplayConfigGetDeviceInfo : en-tête (type, taille, LUID, id) puis le corps.</summary>
    internal static byte[] Paquet(uint type, int taille, long carte, uint id)
    {
        var p = new byte[taille];
        BitConverter.GetBytes(type).CopyTo(p, 0);
        BitConverter.GetBytes((uint)taille).CopyTo(p, 4);
        BitConverter.GetBytes(carte).CopyTo(p, 8);
        BitConverter.GetBytes(id).CopyTo(p, 16);
        return p;
    }

    private static string? NomDeLaCible(long carte, uint id)
    {
        var p = Paquet(2, TailleNomCible, carte, id);
        if (Natif.DisplayConfigGetDeviceInfo(p) != 0) return null;
        var nom = Encoding.Unicode.GetString(p, 36, 128);
        var fin = nom.IndexOf('\0');
        nom = (fin >= 0 ? nom[..fin] : nom).Trim();
        return nom.Length == 0 ? null : nom;
    }
}
