using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// Avant tout conseil sur la limite de puissance, le garde de stabilité des Core Intel de 13e et
/// 14e génération pour ordinateur de bureau. Intel a attribué leur instabilité (« Vmin Shift ») à des tensions trop hautes
/// et l'a corrigée par microcode : 0x129, puis 0x12B (TechPowerUp, 25/09/2024), puis 0x12F (2025), qui complète 0x12B.
/// Aucun ne répare un processeur déjà dégradé. Lu sur la machine de référence : i7-14700F, 0x133.
///
/// Lecture seule : la révision que Windows publie (<c>Update Revision</c>, HKLM\HARDWARE\DESCRIPTION\System\
/// CentralProcessor\0). DrDXT ne met pas le BIOS à jour ; il dit s'il faut le faire.
/// </summary>
public static class MicrocodeRaptorLake
{
    private const uint CorrectifComplet = MicrocodeVmin.CorrectifComplet;

    public static (string? Nom, uint? Revision, string? Identifiant) Lire()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var nom = cle?.GetValue("ProcessorNameString") as string;
            var rev = MicrocodeVmin.Revision(cle?.GetValue("Update Revision") as byte[]);
            return (nom?.Trim(), rev, (cle?.GetValue("Identifier") as string)?.Trim());
        }
        catch (Exception) { return (null, null, null); }
    }

    /// <summary>Pur. Un Core de bureau de 13e ou 14e génération seulement ; tout autre processeur, ou une révision non
    /// lue : rien, jamais une supposition.</summary>
    /// <param name="identifiant">« Intel64 Family 6 Model 183 … » (0xB7, la puce Raptor Lake ;
    /// relevé sur la machine de référence). Un i5-13400 ou 14400 peut être une puce Alder Lake (autre modèle, microcode
    /// numéroté 0x3x) : sans ce garde, « mets à jour le BIOS » lui était dit à tort.</param>
    public static IEnumerable<RepairFinding> Juger(string? nom, uint? revision, string? identifiant)
    {
        // Révision non lue : le docteur se tait (le levier, lui, la tient pour périmée).
        if (revision is null || MicrocodeVmin.Perime(nom, revision, identifiant) is not { } perime) yield break;
        var hex = $"0x{revision!.Value:X}";
        yield return perime
            ? new RepairFinding("CPU", "microcode du processeur", hex, Docteur.T("MC.OuPlusRecent", CorrectifComplet), RepairStatus.Suspicious,
                Docteur.T("MC.Perime", hex, CorrectifComplet),
                RepairAction.Recommandation)
            : new RepairFinding("CPU", "microcode du processeur", hex, Docteur.T("MC.OuPlusRecent", CorrectifComplet), RepairStatus.ExpectedDefault,
                Docteur.T("MC.AJour", hex, CorrectifComplet));
    }
}
