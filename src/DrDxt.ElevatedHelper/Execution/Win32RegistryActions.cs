using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32RegistryActions — the real registry write/delete (m2smoke boundary).
//
// Maps the neutral Core enums (RegistryHiveKind / RegistryValueType) to the
// Microsoft.Win32 enums and writes only the exact (hive, path, name) leaf it is
// handed — which the server already validated leaf-exact. Every failure is
// wrapped → HELPER.OP_FAILED; this never throws.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32RegistryActions : IRegistryActions
{
    public Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)
    {
        try
        {
            using var root = OpenRoot(hive);
            using var key = root.CreateSubKey(path, writable: true);
            if (key is null)
                return Fail($"Could not open registry key '{path}'.");

            switch (kind)
            {
                case RegistryValueType.DWord:
                    if (!TryParseInt(value, out var dword))
                        return Fail("DWORD value is not a valid 32-bit integer.");
                    key.SetValue(name, unchecked((int)dword), RegistryValueKind.DWord);
                    break;
                case RegistryValueType.QWord:
                    if (!long.TryParse(value, out var qword))
                        return Fail("QWORD value is not a valid 64-bit integer.");
                    key.SetValue(name, qword, RegistryValueKind.QWord);
                    break;
                case RegistryValueType.MultiString:
                    // Une entree par ligne. Les lignes vides sont retirees : un REG_MULTI_SZ
                    // avec une entree vide au milieu est traite comme une FIN de liste par
                    // certaines API Windows, et le reste serait silencieusement ignore.
                    key.SetValue(
                        name,
                        value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray(),
                        RegistryValueKind.MultiString);
                    return Result.Ok();

                case RegistryValueType.String:
                    key.SetValue(name, value, RegistryValueKind.String);
                    break;
                default:
                    return Fail("Unsupported registry value kind.");
            }

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Fail($"Registry write failed: {ex.GetType().Name}.");
        }
    }

    public Result DeleteValue(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = OpenRoot(hive);
            using var key = root.OpenSubKey(path, writable: true);
            // A missing key/value means the value is already absent — a clean revert
            // target (its "original" was absence). Treat as success.
            if (key is null)
                return Result.Ok();
            key.DeleteValue(name, throwOnMissingValue: false);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Fail($"Registry delete failed: {ex.GetType().Name}.");
        }
    }

    private static RegistryKey OpenRoot(RegistryHiveKind hive) => hive switch
    {
        RegistryHiveKind.LocalMachine => Registry.LocalMachine,
        RegistryHiveKind.CurrentUser => Registry.CurrentUser,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    // Accept either decimal ("2") or the unsigned form a DWORD tweak may carry.
    private static bool TryParseInt(string value, out uint dword)
    {
        if (uint.TryParse(value, out dword))
            return true;
        if (int.TryParse(value, out var signed))
        {
            dword = unchecked((uint)signed);
            return true;
        }
        return false;
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
