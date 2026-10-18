using System.Runtime.Versioning;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Win32RegistryReader : le vrai IRegistryReader (lecture seule).
//
// Lit un DWORD via Microsoft.Win32.Registry. Une clé ou une valeur manquante donne le
// résultat propre « absent » (Ok(null)), que les leviers savent déjà traiter (leur
// « origine » est alors l'absence). Une vraie erreur de lecture → Fail(TWEAK.SDK_UNAVAILABLE).
// Ne lève jamais. Aucune écriture ici : les écritures passent par le helper élevé.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32RegistryReader : IRegistryReader
{
    public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive switch
            {
                RegistryHiveKind.LocalMachine => Registry.LocalMachine,
                RegistryHiveKind.CurrentUser => Registry.CurrentUser,
                _ => throw new ArgumentOutOfRangeException(nameof(hive)),
            };
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null)
                return Result<uint?>.Ok(null); // absent

            return Dword(key.GetValue(name, defaultValue: null));
        }
        catch (Exception)
        {
            return Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read failed.");
        }
    }

    /// <summary>Seul un vrai REG_DWORD (un int boxé) est un DWORD. Un REG_SZ « 1 » ou un REG_QWORD lu comme tel devenait
    /// l'origine d'un levier, que son annulation réécrivait en REG_DWORD : le type d'origine était perdu, et l'allowlist (un
    /// type par feuille) refuse de le reposer autrement. Lu « pas un DWORD », le levier ne touche à rien.</summary>
    internal static Result<uint?> Dword(object? raw) => raw switch
    {
        null => Result<uint?>.Ok(null), // absent
        int i => Result<uint?>.Ok(unchecked((uint)i)),
        _ => Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry value is not a REG_DWORD."),
    };

    /// <summary>Même règle pour le texte : seul un REG_SZ en est un. Un DWORD rendu en chaîne, ou un REG_EXPAND_SZ que
    /// GetValue rend déjà développé, deviendrait à l'annulation un REG_SZ qui n'était pas là.</summary>
    internal static Result<string?> Texte(object? raw, RegistryValueKind genre) => raw switch
    {
        null => Result<string?>.Ok(null), // absent
        string s when genre == RegistryValueKind.String => Result<string?>.Ok(s),
        _ => Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry value is not a REG_SZ."),
    };

    public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive switch
            {
                RegistryHiveKind.LocalMachine => Registry.LocalMachine,
                RegistryHiveKind.CurrentUser => Registry.CurrentUser,
                _ => throw new ArgumentOutOfRangeException(nameof(hive)),
            };
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null)
                return Result<string?>.Ok(null); // absent

            var raw = key.GetValue(name, defaultValue: null);
            return Texte(raw, raw is null ? RegistryValueKind.Unknown : key.GetValueKind(name));
        }
        catch (Exception)
        {
            return Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read failed.");
        }
    }

    public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = hive switch
            {
                RegistryHiveKind.LocalMachine => Registry.LocalMachine,
                RegistryHiveKind.CurrentUser => Registry.CurrentUser,
                _ => throw new ArgumentOutOfRangeException(nameof(hive)),
            };
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null)
                return Result<string?>.Ok(null); // absent

            // Un REG_MULTI_SZ revient en string[]. Une valeur d'un autre type n'est pas convertie
            // en douce : on rend « absent » plutot qu'une chaine plausible et fausse.
            return key.GetValue(name, defaultValue: null) is string[] entries
                ? Result<string?>.Ok(string.Join('\n', entries))
                : Result<string?>.Ok(null);
        }
        catch (Exception ex)
        {
            return Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, ex.Message);
        }
    }
}
