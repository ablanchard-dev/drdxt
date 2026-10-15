using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// JsonReconcilerStateStore : la persistance du sidecar d'exécution.
//
// Non signé : le sidecar ne porte aucune autorité privilégiée. Tout ce qu'un attaquant pourrait
// y changer ne fait qu'en faire moins au réconciliateur (mettre un élément en pause, sauter la
// réapplication GPU), jamais plus, et ne contourne jamais le manifeste signé. Il vit à côté du
// manifeste sous la même racine en écriture admin, pour résister à l'altération au repos, mais
// l'autorité d'intégrité est la signature du manifeste, pas ce fichier.
//
// Fichier absent → ReconcilerState.Default (premier lancement), pas un échec.
// -----------------------------------------------------------------------
public sealed class JsonReconcilerStateStore : IReconcilerStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonReconcilerStateStore(string path) => _path = path;

    /// <summary>Le chemin par défaut du sidecar sous la racine du manifeste en écriture admin.</summary>
    public static string DefaultPath()
        => Path.Combine(
            Manifest.ProgramDataManifestStorage.DefaultRoot(), "reconciler-state.json");

    /// <summary>Vrai si et seulement si <paramref name="dir"/> est la racine de confiance partagée
    /// du manifeste (<c>%ProgramData%\DrDxt</c>) : le seul dossier dont ce store impose et répare
    /// la protection au repos. Tout autre emplacement (test / surchargé) est traité comme un brouillon
    /// non fiable dont on assure seulement l'existence.</summary>
    private static bool IsSharedTrustRoot(string dir)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Manifest.ProgramDataManifestStorage.DefaultRoot())),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path))
                return Task.FromResult(Result<ReconcilerState>.Ok(ReconcilerState.Default));

            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<ReconcilerState>(json, Options);
            // Un etat ampute se relisait sans erreur (Items nul, build nul) et le service le
            // parcourait au demarrage. Ampute = illisible ; le reconciliateur n'ecrit alors rien.
            if (state is null || state.LastFeatureUpdateBuild is null || state.Items is null
                || state.Items.Any(i => i?.TweakId is null))
                return Task.FromResult(Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, "State read failed: incomplete state."));
            return Task.FromResult(Result<ReconcilerState>.Ok(state));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, $"State read failed: {ex.Message}"));
        }
    }

    public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                if (IsSharedTrustRoot(dir))
                {
                    // Le sidecar vit sous la même racine en écriture admin que manifest-public.key + le manifeste
                    // signé. Que la racine existe ou non, s'assurer qu'elle appartient aux administrateurs (créer /
                    // vérifier / réparer) : un écrivain non élevé (par exemple l'App qui atteint SaveAsync par la
                    // comptabilité du réconciliateur) ne peut ni la créer ni la réparer et échoue proprement : il ne
                    // crée jamais d'avance un %ProgramData%\DrDxt appartenant à l'utilisateur et n'écrit jamais son
                    // sidecar dans une racine squattée par l'utilisateur. Le service élevé / SYSTEM répare ici une
                    // racine squattée avant d'enregistrer le sidecar.
                    var ensured = Manifest.TrustedManifestRoot.EnsureCreatedHardened(dir);
                    if (!ensured.IsOk)
                        return Task.FromResult(ensured);
                }
                else if (!Directory.Exists(dir))
                {
                    // Un emplacement de sidecar non partagé (tests / surcharge explicite) ne porte aucune
                    // confiance : s'assurer seulement que le dossier existe.
                    Directory.CreateDirectory(dir);
                }
            }

            var json = JsonSerializer.Serialize(state, Options);
            var tmp = _path + ".tmp";
            // Un .tmp posé à l'avance (propriétaire, ACL, lien) est supprimé, puis le
            // fichier est créé en exclusif.
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var flux = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var ecrivain = new StreamWriter(flux))
                ecrivain.Write(json);
            File.Move(tmp, _path, overwrite: true);
            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, $"State write failed: {ex.Message}"));
        }
    }
}
