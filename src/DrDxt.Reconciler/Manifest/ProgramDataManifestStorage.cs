using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// ProgramDataManifestStorage : l'IManifestStorage de production.
//
// Défense en profondeur : le manifeste vit sous
//   %ProgramData%\DrDxt\desired-state.manifest
// avec une DACL qui n'accorde l'écriture qu'aux Administrateurs + SYSTEM et la lecture aux
// utilisateurs authentifiés. Un utilisateur non admin peut le lire (l'App le relit sans droits) mais
// pas le réécrire. Cela complète la signature asymétrique, sans jamais la
// remplacer : la signature est l'autorité d'intégrité ; l'ACL ne fait que relever la barre au repos.
//
// Les écritures sont atomiques (un temporaire au nom aléatoire, puis FichierJsonAtomique.Remplacer) :
// un plantage en pleine écriture ne laisse jamais un manifeste à moitié écrit.
//
// Testabilité : le chemin et le descripteur de DACL sont des fonctions statiques pures
// (vérifiées en test unitaire sans système de fichiers ni admin). L'instance se construit avec
// une racine explicite pour que les tests fassent l'aller-retour dans un dossier temporaire ;
// `applyAcl` se désactive pour ces tests.
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
    /// <param name="applyAcl">Poser la DACL en écriture admin à la création (false dans les tests
    /// unitaires, qui tournent sans admin dans un dossier temporaire).</param>
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

    /// <summary>La sécurité durcie de la racine en écriture admin : propriétaire épinglé à
    /// Administrateurs, écriture pour Administrateurs + SYSTEM, lecture pour les utilisateurs
    /// authentifiés ; protégée (n'hérite pas d'une ACL parente plus lâche). Épingler le propriétaire
    /// ferme le trou « le créateur garde un WRITE_DAC implicite et rouvre la DACL » :
    /// voir <see cref="TrustedManifestRoot.BuildHardenedSecurity"/>.</summary>
    public static DirectorySecurity BuildAdminWriteDacl()
        => TrustedManifestRoot.BuildHardenedSecurity();

    public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(envelope);

            if (_applyAcl)
            {
                // Production : écriture durcie. S'assurer de la racine appartenant aux administrateurs (ou la
                // réparer), écrire atomiquement, et épingler owner=Administrators + DACL protégée réservée aux
                // admins sur le fichier du manifeste, pour que le réconciliateur SYSTEM lui fasse confiance au
                // repos même quand l'écrivain est un utilisateur admin (pas SYSTEM). Échoue bruyamment si le
                // propriétaire ne peut pas être posé (appelant non élevé) : jamais de manifeste appartenant à
                // l'utilisateur, que le service rejetterait en silence.
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

    /// <summary>La reprise : le manifeste illisible est renomme a cote de lui, jamais efface
    /// (il reste la seule trace de ce qu'il contenait). Absent ⇒ rien a faire. Le renommage garde le
    /// descripteur de securite du fichier.</summary>
    public Result MettreDeCote() => MiseDeCote.Renommer(FilePath);

    /// <summary>Apres un rendu a la desinstallation. Laisse en place, le manifeste ferait retrouver a
    /// une reinstallation des origines deja rendues, capturees avant la desinstallation
    /// precedente, et le jour ou le service les rejouerait il reecrirait un passe que l'utilisateur a
    /// quitte. Il est renomme `desired-state.manifest.rendu-<heure UTC>`, jamais efface : il reste la
    /// trace de ce que la desinstallation a lu.</summary>
    public Result MettreDeCoteApresRendu() => MiseDeCote.Renommer(FilePath, "rendu");

    public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(FilePath))
                return Task.FromResult(Result<SignedManifestEnvelope?>.Ok(null));

            // Intégrité au repos : avant de faire confiance à un manifeste présent, vérifier
            // que son propriétaire est Administrateurs/SYSTEM et que la racine n'est pas inscriptible par un
            // non-admin. Une racine empoisonnée (par exemple un %ProgramData%\DrDxt créé par l'utilisateur)
            // → fail-closed (traitée comme altérée) : rien n'est réappliqué. Défense en profondeur au-dessus
            // de la signature Ed25519 (l'autorité d'intégrité). Sautée seulement en mode test / sans ACL.
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
