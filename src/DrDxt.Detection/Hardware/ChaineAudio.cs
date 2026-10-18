namespace DrDxt.Detection.Hardware;

/// <summary>
/// Une chaîne audio virtuelle (câble ou mélangeur logiciel) déclarée comme périphérique son : le son du jeu y passe par
/// un logiciel de plus, qui coûte de la latence audio et un peu de processeur, et peut fausser une mesure. Reconnue par le
/// nom des périphériques son (Win32_SoundDevice), sans rien ouvrir ni changer. Les noms sont ceux des produits connus
/// (VB-Audio et ses câbles, Voicemeeter, SteelSeries Sonar, Virtual Audio Cable), relevés sur la machine de référence pour
/// VB-Audio (« VB-Audio Hi-Fi Cable », le 16/10/2026).
/// </summary>
public static class ChaineAudio
{
    private static readonly string[] Motifs = ["VB-Audio", "Voicemeeter", "CABLE Input", "CABLE Output", "Hi-Fi Cable", "Sonar", "Virtual Audio Cable"];

    /// <summary>Pur : les noms de périphériques son qui sont une chaîne virtuelle.</summary>
    public static IReadOnlyList<string> Filtrer(IEnumerable<string> noms)
        => noms.Where(n => Motifs.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();

    /// <summary>Le vrai, en lecture seule ; <c>null</c> = WMI n'a pas répondu.</summary>
    public static IReadOnlyList<string>? Virtuelles()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_SoundDevice");
            var noms = new List<string>();
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    if (o["Name"]?.ToString() is { } n) noms.Add(n);
            return Filtrer(noms);
        }
        catch (Exception) { return null; }
    }
}
