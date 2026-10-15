using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Le pur du garde « Vmin Shift » des Core Intel de 13e et 14e génération pour ordinateur de bureau : Intel a attribué leur
/// instabilité à des tensions trop hautes et l'a corrigée par microcode (0x129, 0x12B, puis 0x12F en 2025, qui complète
/// 0x12B ; TechPowerUp, 25/09/2024). Partagé par le docteur (le dire) et par le levier de puissance (ne pas
/// monter la PL1 sur un ancien microcode), qui ne le consultait pas.
/// </summary>
public static class MicrocodeVmin
{
    /// <summary>Le dernier des trois correctifs d'Intel pour le Vmin Shift.</summary>
    public const uint CorrectifComplet = 0x12F;

    // Core i5, i7, i9 de 13e ou 14e génération, bureau (sans suffixe mobile H, HX, U, P).
    private static readonly Regex Bureau = new(@"\bi[579]-1[34]\d{3}(K|KF|KS|F|T)?\b", RegexOptions.Compiled);

    /// <summary>Vrai = processeur concerné et microcode antérieur au correctif, ou concerné et révision non lue
    /// (« pas lu » ne doit pas laisser monter la puissance) ; faux = concerné et corrigé ; null = pas concerné.
    /// Concerné = nom de Core de bureau 13e/14e et puce Raptor Lake (« Family 6 Model 183 ») : un i5-13400 ou 14400 peut être
    /// une puce Alder Lake, au microcode numéroté autrement.</summary>
    public static bool? Perime(string? nom, uint? revision, string? identifiant)
    {
        if (!Concerne(nom, identifiant)) return null;
        return revision is not { } rev || rev < CorrectifComplet;
    }

    /// <summary>Pur : la révision du microcode dans la valeur « Update Revision » du registre (CentralProcessor 0 sous HKLM HARDWARE DESCRIPTION System) : 8 octets, la révision dans les 4 de poids fort ; 4 octets, la révision seule (selon la version de
    /// Windows). Null si la valeur manque ou est trop courte. Le seul décodage (App et helper).</summary>
    public static uint? Revision(byte[]? brut)
        => brut is null ? null : brut.Length >= 8 ? BitConverter.ToUInt32(brut, 4) : brut.Length >= 4 ? BitConverter.ToUInt32(brut, 0) : null;

    public static bool Concerne(string? nom, string? identifiant)
        => nom is not null && nom.Contains("Intel", StringComparison.OrdinalIgnoreCase) && Bureau.IsMatch(nom)
           && identifiant is not null
           && (identifiant.Contains("Family 6 Model 183 ", StringComparison.Ordinal) || identifiant.EndsWith("Family 6 Model 183", StringComparison.Ordinal));
}
