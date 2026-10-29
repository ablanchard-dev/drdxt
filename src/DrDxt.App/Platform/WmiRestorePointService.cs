using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// WmiRestorePointService : le vrai IRestorePointService.
//
// * IsEnabled() : utilisable quand la configuration annonce un espace disque non nul pour le lecteur système (WMI
//   root\default:SystemRestoreConfig, repli sur le registre). Lecture seule.
// * TryEnable() : par le helper élevé (EnableSystemRestoreOp, lecteur système seulement), après le consentement du
//   SafetyManager (IConsentPrompt.AskEnableRestore), puis IsEnabled() revérifié.
// * VerifyPointExists(desc) : une description correspondante dans les points récents (root\default:SystemRestore), la
//   garde contre une création qui ne fait rien en silence.
// La création passe par le helper (CreateRestorePointOp). Rien ne lève.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class WmiRestorePointService : IRestorePointService
{
    private const string SystemRestoreScope = @"\\.\root\default";

    private readonly IElevatedClient _elevated;

    public WmiRestorePointService(IElevatedClient elevated) =>
        _elevated = elevated ?? throw new ArgumentNullException(nameof(elevated));

    public bool InterditeParStrategie()
    {
        try
        {
            using var cle = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
            return cle?.GetValue("DisableSR") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    public bool IsEnabled()
    {
        // D'abord : WMI SystemRestoreConfig (RPSessionInterval > 0 veut dire restauration active), sous borne ; illisible ou
        // figé, la lecture du registre décide.
        if (LectureWmiBornee.Lire(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        SystemRestoreScope, "SELECT RPSessionInterval FROM SystemRestoreConfig");
                    using var rangees = searcher.Get();
                    foreach (var o in rangees)
                    {
                        using (o)
                        {
                            var raw = o["RPSessionInterval"];
                            if (raw is not null && int.TryParse(raw.ToString(), out var interval) && interval > 0)
                                return true;
                        }
                    }
                }
                catch
                {
                    // on passe à la lecture du registre
                }
                return false;
            }, () => false))
            return true;

        // Repli : la clé de registre de configuration de la restauration (RPSessionInterval).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: false);
            var value = key?.GetValue("RPSessionInterval");
            return value is int i && i > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryEnable()
    {
        // Déjà utilisable ? Rien à faire.
        if (IsEnabled())
            return true;

        try
        {
            // Par le helper (EnableSystemRestoreOp, lecteur système), puis revérifié : une GPO ou l'édition de Windows peut
            // encore l'annuler.
            var drive = SystemDrive();
            var result = _elevated
                .ExecuteAsync(new EnableSystemRestoreOp(drive), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!result.IsOk)
                return false;

            return IsEnabled();
        }
        catch
        {
            // Jamais d'exception à travers cette frontière : une activation ratée dit simplement « inactive ».
            return false;
        }
    }

    // Le lecteur système en « X: » depuis %SystemDrive%, barre finale retirée ; repli « C: ».
    private static string SystemDrive()
    {
        try
        {
            var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (!string.IsNullOrWhiteSpace(sysDrive))
                return sysDrive.TrimEnd('\\', '/');
        }
        catch
        {
            // on passe à la valeur par défaut
        }
        return "C:";
    }

    public DateTime? DernierPointDepuis(DateTime depuisUtc) => LectureWmiBornee.Lire(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                SystemRestoreScope, "SELECT CreationTime FROM SystemRestore");
            var creations = new List<string?>();
            using var rangees = searcher.Get();
            foreach (var o in rangees)
            {
                using (o) creations.Add(o["CreationTime"]?.ToString());
            }
            return PlusRecentDepuis(creations, depuisUtc);
        }
        catch
        {
            return null;
        }
    }, () => (DateTime?)null);

    /// <summary>Le point le plus recent cree depuis <paramref name="depuisUtc"/>, en UTC, ou <c>null</c>.
    /// Dates de la classe WMI SystemRestore (« 20260911222955.512634-000 », relevée sur un poste réel) ; une date illisible
    /// est ignoree, un point d'age inconnu ne prouve rien.</summary>
    internal static DateTime? PlusRecentDepuis(IEnumerable<string?> creationsDmtf, DateTime depuisUtc)
    {
        DateTime? plusRecent = null;
        foreach (var dmtf in creationsDmtf)
        {
            if (string.IsNullOrWhiteSpace(dmtf)) continue;
            DateTime utc;
            try { utc = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime(); }
            catch { continue; }
            if (utc >= depuisUtc && (plusRecent is null || utc > plusRecent)) plusRecent = utc;
        }
        return plusRecent;
    }
}
