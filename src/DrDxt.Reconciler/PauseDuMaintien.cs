using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.Reconciler;

public enum EtatDeLaPause
{
    Actif,
    EnPause,
    /// <summary>Un fichier est là, mais son propriétaire, ses droits ou son dossier ne sont pas ceux des administrateurs.
    /// Il vaut pause : dans le doute, le maintien n'écrit rien.</summary>
    NonFiable,
}

/// <summary>
/// La pause du maintien que le joueur pose depuis l'App : le réconciliateur ne réapplique plus rien.
///
/// Un fichier à part, et non un champ du sidecar : le service charge, compare et sauve le sidecar à chaque passe, et
/// une pause posée entre son chargement et sa sauvegarde y serait écrasée. Ce fichier-ci, seul le helper élevé l'écrit
/// (sa présence = en pause) ; le service le lit seulement. Il ne peut qu'en faire moins : rien n'y est réécrit, la
/// reprise rend la main au manifeste signé.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PauseDuMaintien
{
    public const string NomFichier = "pause-du-maintien";

    private readonly string _racine;
    private readonly string _chemin;
    private readonly bool _verifierConfiance;

    /// <param name="verifierConfiance">Toujours vrai en production. Faux seulement pour les tests, qui
    /// tournent dans un dossier temporaire que rien ne rend fiable.</param>
    public PauseDuMaintien(string racine, bool verifierConfiance)
    {
        _racine = racine;
        _chemin = Path.Combine(racine, NomFichier);
        _verifierConfiance = verifierConfiance;
    }

    public static PauseDuMaintien ParDefaut() => new(ProgramDataManifestStorage.DefaultRoot(), verifierConfiance: true);

    public Result Poser(bool enPause)
    {
        try
        {
            if (!enPause)
            {
                if (File.Exists(_chemin)) File.Delete(_chemin);
                return Result.Ok();
            }
            if (_verifierConfiance) return TrustedManifestRoot.WriteTrustedFile(_chemin, "1"u8.ToArray());
            Directory.CreateDirectory(_racine);
            File.WriteAllBytes(_chemin, "1"u8.ToArray());
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }

    public EtatDeLaPause Lire()
    {
        if (!File.Exists(_chemin)) return EtatDeLaPause.Actif;
        if (_verifierConfiance && !TrustedManifestRoot.EnsureTrustedFile(_chemin).IsOk) return EtatDeLaPause.NonFiable;
        return EtatDeLaPause.EnPause;
    }
}
