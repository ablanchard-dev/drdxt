using System.Text.RegularExpressions;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// 04/10/2026 (plan 2.2) : avant tout conseil sur la limite de puissance, le garde de stabilité des Core Intel de 13e et
/// 14e génération pour ordinateur de bureau. Intel a attribué leur instabilité (« Vmin Shift ») à des tensions trop hautes
/// et l'a corrigée par microcode : 0x129, puis 0x12B (TechPowerUp, 25/09/2024), puis 0x12F (2025), qui complète 0x12B.
/// Aucun ne répare un processeur déjà dégradé. Lu sur la machine de référence : i7-14700F, 0x133.
///
/// Lecture seule : la révision que Windows publie (<c>Update Revision</c>, HKLM\HARDWARE\DESCRIPTION\System\
/// CentralProcessor\0). DrDXT ne met pas le BIOS à jour ; il dit s'il faut le faire.
/// </summary>
public static class MicrocodeRaptorLake
{
    /// <summary>Le dernier des trois correctifs d'Intel pour le Vmin Shift.</summary>
    public const uint CorrectifComplet = 0x12F;

    // Core i5, i7, i9 de 13e ou 14e génération, bureau (sans suffixe mobile H, HX, U, P).
    private static readonly Regex Bureau = new(@"\bi[579]-1[34]\d{3}(K|KF|KS|F|T)?\b", RegexOptions.Compiled);

    public static (string? Nom, uint? Revision, string? Identifiant) Lire()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var nom = cle?.GetValue("ProcessorNameString") as string;
            // 4 octets (révision seule) ou 8 octets (révision dans les 4 de poids fort), selon la version de Windows.
            uint? rev = cle?.GetValue("Update Revision") is byte[] b
                ? b.Length >= 8 ? BitConverter.ToUInt32(b, 4) : b.Length >= 4 ? BitConverter.ToUInt32(b, 0) : null
                : null;
            return (nom?.Trim(), rev, (cle?.GetValue("Identifier") as string)?.Trim());
        }
        catch (Exception) { return (null, null, null); }
    }

    /// <summary>Pur. Un Core de bureau de 13e ou 14e génération seulement ; tout autre processeur, ou une révision non
    /// lue : rien, jamais une supposition.</summary>
    /// <param name="identifiant">Revue du 04/10 (P1) : « Intel64 Family 6 Model 183 … » (0xB7, la puce Raptor Lake ;
    /// relevé sur la machine de référence). Un i5-13400 ou 14400 peut être une puce Alder Lake (autre modèle, microcode
    /// numéroté 0x3x) : sans ce garde, « mets à jour le BIOS » lui était dit à tort.</param>
    public static IEnumerable<RepairFinding> Juger(string? nom, uint? revision, string? identifiant)
    {
        if (nom is null || revision is not { } rev || !nom.Contains("Intel", StringComparison.OrdinalIgnoreCase) || !Bureau.IsMatch(nom)
            || identifiant is null || !identifiant.Contains("Family 6 Model 183 ", StringComparison.Ordinal) && !identifiant.EndsWith("Family 6 Model 183", StringComparison.Ordinal))
            yield break;
        var hex = $"0x{rev:X}";
        yield return rev < CorrectifComplet
            ? new RepairFinding("CPU", "microcode du processeur", hex, $"0x{CorrectifComplet:X} ou plus récent", RepairStatus.Suspicious,
                $"le microcode de ton processeur ({hex}) est antérieur au correctif d'Intel contre l'instabilité des Core de 13e et "
                + $"14e génération (0x{CorrectifComplet:X}, 2025). Mets à jour le BIOS de ta carte depuis le site du fabricant avant "
                + "tout réglage de puissance : plus de puissance sur un ancien microcode, c'est le risque que ce correctif retire.",
                RepairAction.Recommandation)
            : new RepairFinding("CPU", "microcode du processeur", hex, $"0x{CorrectifComplet:X} ou plus récent", RepairStatus.ExpectedDefault,
                $"le microcode de ton processeur ({hex}) contient le correctif d'Intel contre l'instabilité des Core de 13e et 14e "
                + $"génération (0x{CorrectifComplet:X} ou plus récent). Il protège pour la suite, il ne répare pas un processeur déjà abîmé.");
    }
}
