using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Display;

/// <summary>
/// Les superpositions matérielles (plans multiples, MPO) coupées par un ancien réglage : sans elles, un jeu en fenêtre
/// sans bordure perd le flip indépendant et passe par la composition de Windows. Lecture seule ; DrDXT ne pose ni ne
/// retire cette valeur.
/// </summary>
/// <remarks>
/// Source : NVIDIA, article de support 5157 (MPO sous Windows 11, mis à jour le 28/09/2026) ; son fichier mpo_disable.reg
/// pose « OverlayTestMode »=dword:00000005 sous HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm, et mpo_restore.reg
/// retire la valeur. Une valeur absente est donc le réglage d'origine de Windows, pas une lecture manquée. Relevé sur la
/// machine de référence le 17/10/2026 : valeur absente.
/// </remarks>
public static class SuperpositionsMaterielles
{
    public const string Cle = @"SOFTWARE\Microsoft\Windows\Dwm";
    public const string Valeur = "OverlayTestMode";
    public const int Coupees = 5;

    /// <summary>(lu, valeur) : lu = la clé a pu s'ouvrir ; valeur null = absente.</summary>
    public static (bool Lu, int? Valeur) Lire()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(Cle);
            // La clé Dwm existe sur toute installation de Windows : introuvable, c'est une lecture empêchée.
            if (cle is null) return (false, null);
            return (true, cle.GetValue(Valeur) is int v ? v : null);
        }
        catch (Exception) { return (false, null); }
    }

    public static RepairFinding Juger(bool lu, int? valeur)
    {
        if (!lu)
            return new RepairFinding("DISPLAY", "superpositions materielles", null, null, RepairStatus.Unknown, Docteur.T("MPO.NonLu"));
        return valeur switch
        {
            null => new RepairFinding("DISPLAY", "superpositions materielles", Docteur.T("MPO.Defaut"), null, RepairStatus.ExpectedDefault,
                Docteur.T("MPO.Absente")),
            Coupees => new RepairFinding("DISPLAY", "superpositions materielles", $"{Valeur} = {Coupees}", Docteur.T("MPO.Defaut"),
                RepairStatus.Suspicious, Docteur.T("MPO.Coupees"), RepairAction.Recommandation),
            _ => new RepairFinding("DISPLAY", "superpositions materielles", $"{Valeur} = {valeur}", Docteur.T("MPO.Defaut"),
                RepairStatus.Observed, Docteur.T("MPO.Autre", valeur)),
        };
    }
}
