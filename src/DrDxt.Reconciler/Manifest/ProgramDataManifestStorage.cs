using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// ProgramDataManifestStorage : l'IManifestStorage de production, %ProgramData%\DrDxt\desired-state.manifest. DACL :
// écriture pour Administrateurs + SYSTEM, lecture pour les utilisateurs authentifiés (l'App relit sans droits). En
// défense en profondeur : la signature reste l'autorité d'intégrité, l'ACL relève la barre au repos. Écritures
// atomiques (temporaire au nom aléatoire, puis FichierJsonAtomique.Remplacer). Chemin et DACL sont statiques purs ; la
// racine explicite et `applyAcl` désactivable servent aux tests en dossier temporaire.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class ProgramDataManifestStorage : IManifestStorage
{
    private const string FileName = NomDuFichier;

    /// <summary>Le nom du manifeste dans sa racine (l'installeur et la désinstallation le cherchent aussi).</summary>
    public const string NomDuFichier = "desired-state.manifest";

    private readonly string _root;
    private readonly bool _applyAcl;

    public ProgramDataManifestStorage() : this(DefaultRoot(), applyAcl: true) { }

    /// <param name="root">Le dossier où vit le manifeste.</param>
    /// <param name="applyAcl">Poser la DACL en écriture admin (false dans les tests, sans admin).</param>
    public ProgramDataManifestStorage(string root, bool applyAcl)
    {
        _root = root;
        _applyAcl = applyAcl;
    }

    private string FilePath => Path.Combine(_root, FileName);

    /// <summary>%ProgramData%\DrDxt : la racine en écriture admin.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DrDxt");

    /// <summary>La sécurité durcie de la racine : propriétaire Administrateurs (le créateur ne rouvre pas la DACL par son
    /// WRITE_DAC implicite), écriture Administrateurs + SYSTEM, lecture utilisateurs authentifiés, sans héritage. Voir
    /// <see cref="TrustedManifestRoot.BuildHardenedSecurity"/>.</summary>
    public static DirectorySecurity BuildAdminWriteDacl()
        => TrustedManifestRoot.BuildHardenedSecurity();

    public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope);

            if (_applyAcl)
            {
                // Production : racine aux administrateurs (ou réparée), écriture atomique, owner=Administrators et DACL
                // protégée sur le fichier, pour que SYSTEM lui fasse confiance même écrit par un admin. Propriétaire
                // impossible à poser (non élevé) : échec, jamais un manifeste à l'utilisateur.
                return Task.FromResult(
                    TrustedManifestRoot.WriteTrustedFile(FilePath, Encoding.UTF8.GetBytes(json)));
            }

            // Mode test / sans ACL (dossier temporaire possédé par l'utilisateur de test) : simple écriture atomique.
            return Task.FromResult(new FichierJsonAtomique(FilePath, ErrorCodes.IoFailure).WriteAtomic(json));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, $"Manifest write failed: {ex.Message}"));
        }
    }

    /// <summary>La reprise : le manifeste illisible est renomme a cote, jamais efface (la seule trace de son contenu),
    /// descripteur de securite garde. Absent ⇒ rien a faire.</summary>
    public Result MettreDeCote() => MiseDeCote.Renommer(FilePath);

    /// <summary>Apres un rendu a la desinstallation : laisse en place, le manifeste ferait retrouver a une reinstallation
    /// des origines deja rendues, que le service pourrait rejouer. Renomme `desired-state.manifest.rendu-<heure UTC>`,
    /// jamais efface : la trace de ce que la desinstallation a lu.</summary>
    public Result MettreDeCoteApresRendu() => MiseDeCote.Renommer(FilePath, "rendu");

    public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(FilePath))
                return Task.FromResult(Result<SignedManifestEnvelope?>.Ok(null));

            // Intégrité au repos, en plus de la signature Ed25519 : propriétaire Administrateurs/SYSTEM et racine non
            // inscriptible par un non-admin. Racine empoisonnée (un %ProgramData%\DrDxt créé par l'utilisateur) →
            // traitée comme altérée, rien n'est réappliqué. Sautée seulement sans ACL (tests).
            if (_applyAcl)
            {
                var trust = TrustedManifestRoot.EnsureTrustedFile(FilePath);
                if (!trust.IsOk)
                    return Task.FromResult(Result<SignedManifestEnvelope?>.Fail(
                        ErrorCodes.ManifestTampered,
                        $"Manifest root is not admin-trusted at rest: {trust.Error!.Value.Message}"));
            }

            var json = File.ReadAllText(FilePath);
            var env = JsonSerializer.Deserialize<SignedManifestEnvelope>(json);
            // Un fichier present qui contient « null » n'est pas un fichier absent.
            return Task.FromResult(env is null
                ? Result<SignedManifestEnvelope?>.Fail(ErrorCodes.ManifestTampered, "Manifest file is present but empty.")
                : Result<SignedManifestEnvelope?>.Ok(env));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<SignedManifestEnvelope?>.Fail(ErrorCodes.IoFailure, $"Manifest read failed: {ex.Message}"));
        }
    }
}
