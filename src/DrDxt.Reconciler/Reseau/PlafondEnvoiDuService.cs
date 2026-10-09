using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.Reconciler.Reseau;

/// <summary>Le jeu à protéger et le plafond que la mesure a donné.</summary>
public sealed record PlafondRetenuDuService(string Exe, long PlafondBps);

/// <summary>
/// 28/09/2026 : la protection du ping, retenue pour le service de démarrage.
///
/// L'App la gardait sous %LOCALAPPDATA% (<c>PlafondEnvoiMemorise</c>) : un fichier que tout programme du
/// compte peut réécrire, et que le helper appliquait : un programme du compte pouvait y nommer un autre exe. Le service tourne en SYSTEM, sans
/// personne devant l'écran : il ne lit donc que %ProgramData%\DrDxt, écrit par le helper élevé, dont le
/// propriétaire et les droits sont vérifiés à chaque lecture. Un fichier non fiable, abîmé ou incohérent
/// n'allume rien.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PlafondEnvoiDuService
{
    public const string NomFichier = "plafond-envoi.json";

    private readonly string _chemin;
    private readonly bool _verifierConfiance;

    /// <param name="verifierConfiance">Toujours vrai en production. Faux seulement pour les tests, qui
    /// tournent dans un dossier temporaire que rien ne rend fiable.</param>
    public PlafondEnvoiDuService(string racine, bool verifierConfiance)
    {
        _chemin = Path.Combine(racine, NomFichier);
        _verifierConfiance = verifierConfiance;
    }

    public static PlafondEnvoiDuService ParDefaut() => new(ProgramDataManifestStorage.DefaultRoot(), verifierConfiance: true);

    public Result Retenir(PlafondRetenuDuService retenu)
    {
        var octets = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(retenu));
        if (_verifierConfiance) return TrustedManifestRoot.WriteTrustedFile(_chemin, octets);
        try
        {
            File.WriteAllBytes(_chemin, octets);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }

    public Result Oublier()
    {
        try
        {
            if (File.Exists(_chemin)) File.Delete(_chemin);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }

    /// <summary>La protection à porter, ou <c>null</c> : rien de retenu, fichier non fiable, abîmé ou incohérent.</summary>
    public PlafondRetenuDuService? Lire()
    {
        try
        {
            if (!File.Exists(_chemin)) return null;
            if (_verifierConfiance && !TrustedManifestRoot.EnsureTrustedFile(_chemin).IsOk) return null;
            var r = JsonSerializer.Deserialize<PlafondRetenuDuService>(File.ReadAllText(_chemin));
            return r is { PlafondBps: > 0 } && NomExeDeJeu.EstUnJeuPossible(r.Exe) ? r : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
