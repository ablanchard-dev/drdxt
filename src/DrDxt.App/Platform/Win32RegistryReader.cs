using System.Runtime.Versioning;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Win32RegistryReader — the real IRegistryReader (read-only).
//
// Reads a DWORD via Microsoft.Win32.Registry. A missing key/value is the clean
// "absent" result (Ok(null)) the tweaks already handle (its "original" is
// absence). A genuine read error → Fail(TWEAK.SDK_UNAVAILABLE). Never throws.
// No write path lives here — writes go through the elevated helper.
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

            var raw = key.GetValue(name, defaultValue: null);
            if (raw is null)
                return Result<uint?>.Ok(null); // absent

            // DWORD comes back as a boxed int; normalize to uint.
            return raw switch
            {
                int i => Result<uint?>.Ok(unchecked((uint)i)),
                long l => Result<uint?>.Ok(unchecked((uint)l)),
                _ => uint.TryParse(raw.ToString(), out var u)
                    ? Result<uint?>.Ok(u)
                    : Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry value is not a DWORD."),
            };
        }
        catch (Exception)
        {
            return Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "Registry read failed.");
        }
    }

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
            // Absent → Ok(null). Present → its string form (REG_SZ comes back as a string).
            return Result<string?>.Ok(raw?.ToString());
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
