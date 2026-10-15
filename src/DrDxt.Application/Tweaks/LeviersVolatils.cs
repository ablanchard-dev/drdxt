namespace DrDxt.Application.Tweaks;

/// <summary>
/// Les leviers que le redémarrage efface (le pilote ou le BIOS remettent leur valeur) : la limite de puissance
/// de la carte (mesuré sur la machine de référence : posée à 90 % le 10/10, relue à 100 % par nvidia-smi après redémarrage) et la limite
/// continue du processeur (MSR 0x610, montée ou plafond). Après un redémarrage, leur état est connu sans rien lire ni
/// écrire : « pas posé ». Les autres (registre, plan d'alimentation, réglages du pilote) survivent au redémarrage.
/// </summary>
public static class LeviersVolatils
{
    private static readonly HashSet<string> Ids = ["gpu.power_limit", "fixit.cpu_power_limit", "fixit.cpu_power_cap"];

    public static bool Contient(string levierId) => Ids.Contains(levierId);
}
