using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// JsonReconcilerStateStore — the runtime sidecar persistence (plan §5 / T14).
//
// Not signed: the sidecar carries no privileged authority. Anything an attacker
// could change here only makes the reconciler do LESS (pause an item, skip GPU
// re-apply) — never more, and never bypasses the signed manifest. It lives next to
// the manifest under the same admin-write root for tamper-resistance at rest, but
// the integrity authority is the manifest signature, not this file.
//
// Absent file → ReconcilerState.Default (first run), not a failure.
// -----------------------------------------------------------------------
public sealed class JsonReconcilerStateStore : IReconcilerStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonReconcilerStateStore(string path) => _path = path;

    /// <summary>The default sidecar path under the admin-write manifest root.</summary>
    public static string DefaultPath()
        => Path.Combine(
            Manifest.ProgramDataManifestStorage.DefaultRoot(), "reconciler-state.json");

    /// <summary>True iff <paramref name="dir"/> is the shared manifest trust root
    /// (<c>%ProgramData%\DrDxt</c>) — the only directory whose at-rest posture this store
    /// enforces/repairs. Any other (test / overridden) location is treated as untrusted
    /// scratch and only ensured to exist.</summary>
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
            // 26/09/2026 : un etat ampute se relisait sans erreur (Items nul, build nul) et le service le
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
                    // The sidecar lives under the same admin-write root as manifest-public.key
                    // + the signed manifest. Whether the root exists or not, ensure it is
                    // admin-owned (create / verify / REPAIR): a non-elevated writer (e.g. the
                    // App reaching SaveAsync via the reconciler's bookkeeping) cannot create or
                    // repair it and fails CLEAN — it never pre-creates a user-owned
                    // %ProgramData%\DrDxt nor writes its sidecar into a pre-squatted user-owned
                    // root (BLOCKER #1 / F1). The elevated/SYSTEM service SELF-HEALS a squatted
                    // root here before persisting the sidecar.
                    var ensured = Manifest.TrustedManifestRoot.EnsureCreatedHardened(dir);
                    if (!ensured.IsOk)
                        return Task.FromResult(ensured);
                }
                else if (!Directory.Exists(dir))
                {
                    // A non-shared sidecar location (tests / explicit override) carries no
                    // trust — just ensure the directory exists.
                    Directory.CreateDirectory(dir);
                }
            }

            var json = JsonSerializer.Serialize(state, Options);
            var tmp = _path + ".tmp";
            // Revue de sécurité du 08/10 : un .tmp posé à l'avance (propriétaire, ACL, lien) est supprimé, puis le
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
