using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32RegistryActions : la vraie écriture / suppression dans le registre (frontière du m2smoke).
//
// Relie les enums neutres de Core (RegistryHiveKind / RegistryValueType) aux enums
// Microsoft.Win32 et n'écrit que la feuille exacte (ruche, chemin, nom) reçue, déjà validée à
// la feuille près par le serveur. Tout échec est emballé → HELPER.OP_FAILED ; ne lève jamais.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32RegistryActions(Func<RegistryHiveKind, TraceDesCoupures?>? traces = null) : IRegistryActions
{
    /// <summary>Le helper tient la trace des coupures de la machine ; l'App, celle de la session qu'elle écrit elle-même.</summary>
    public static TraceDesCoupures? TraceDuHelper(RegistryHiveKind ruche) => ruche == RegistryHiveKind.LocalMachine ? TraceDesCoupures.DeLaMachine() : null;

    public static TraceDesCoupures? TraceDeLaSession(RegistryHiveKind ruche) => ruche == RegistryHiveKind.CurrentUser ? TraceDesCoupures.DeLaSession() : null;

    // Les trois exécutants (helper, désinstallation, service) écrivent par ici : la garde du fichier d'échange les couvre tous.
    private readonly GardeDuFichierDEchange _fichierDEchange = new();

    public Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)
    {
        if (!GardeDuFichierDEchange.Vise(hive, path, name)) return Ecrire(hive, path, name, kind, value);
        if (_fichierDEchange.Avant(value) is { } refus) return Fail(refus);
        var r = Ecrire(hive, path, name, kind, value);
        if (r.IsOk) _fichierDEchange.Apres(value);
        return r;
    }

    private static Result Ecrire(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)
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
                    // avec une entree vide au milieu est traite comme une fin de liste par
                    // certaines API Windows, et le reste serait silencieusement ignore.
                    key.SetValue(
                        name,
                        MultiTexteDuRegistre.Entrees(value),
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

    private const string Approuve = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Le programme doit être listé là où sa liste le dit : sans ce contrôle, un op forgé créerait des valeurs pour des
    // programmes qui n'existent pas. Le programme lui-même (sa commande, son raccourci) n'est jamais touché.
    public Result SetStartupApproval(RegistryHiveKind hive, string liste, string nom, bool actif)
    {
        try
        {
            using var root = OpenRoot(hive);
            var estListe = liste switch
            {
                SetStartupApprovalOp.Run => Liste(root, @"Software\Microsoft\Windows\CurrentVersion\Run", nom),
                SetStartupApprovalOp.Run32 => Liste(root, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", nom),
                SetStartupApprovalOp.DossierDemarrage => File.Exists(Path.Combine(Environment.GetFolderPath(
                    hive == RegistryHiveKind.CurrentUser ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup), nom)),
                _ => false,
            };
            if (!estListe)
                return Fail("This program is not listed at startup.");
            using var key = root.CreateSubKey(Approuve + liste, writable: true);
            if (key is null)
                return Fail("Could not open the StartupApproved key.");
            var octets = SetStartupApprovalOp.Octets(actif, DateTime.UtcNow);
            // Une coupure est notée avant d'être écrite : l'App tuée juste après sait encore la réactiver.
            return TraceDesCoupures.Autour(traces?.Invoke(hive), new CoupureDuDemarrage(hive, liste, nom, Convert.ToHexString(octets)), actif,
                () =>
                {
                    try { key.SetValue(nom, octets, RegistryValueKind.Binary); return Result.Ok(); }
                    catch (Exception ex) { return Fail($"Startup approval write failed: {ex.GetType().Name}."); }
                });
        }
        catch (Exception ex)
        {
            return Fail($"Startup approval write failed: {ex.GetType().Name}.");
        }

        static bool Liste(RegistryKey racine, string run, string nom)
        {
            using var k = racine.OpenSubKey(run);
            return k?.GetValue(nom) is not null;
        }
    }

    public Result DeleteValue(RegistryHiveKind hive, string path, string name)
    {
        try
        {
            using var root = OpenRoot(hive);
            using var key = root.OpenSubKey(path, writable: true);
            // Une clé ou une valeur absente veut dire que la valeur est déjà absente : une cible de retour
            // propre (son « original » était l'absence). Traiter comme un succès.
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

    // Accepter le décimal (« 2 ») ou la forme non signée que peut porter un levier DWORD.
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
