using System.Globalization;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// SignedDesiredStateStore — the concrete IDesiredStateStore (DrDxt.Core).
//
// COMMIT (app side, holds the PRIVATE key via IManifestSigner):
//   1. Reject any tweak that is not OS-side reconcilable (firmware / unknown /
//      protected) — durability scope §4 P4 (T7).
//   2. Canonicalize the versioned manifest to deterministic bytes.
//   3. Sign the canonical bytes; persist {canonical, signature} ATOMICALLY via the
//      admin-write storage seam (T6).
//
// LOAD (reconciler side, holds only the PUBLIC key via IManifestVerifier):
//   1. No file → empty DesiredState (first install, T4) — not a failure.
//   2. VERIFY the signature over the exact persisted canonical bytes before parsing.
//      Any tamper / foreign signature → Fail(ManifestTampered), no DesiredState
//      (fail-CLOSED, never fail-open — master §9.2 / I4, T3/T3b).
//   3. Parse + check schemaVersion; unknown version → Fail(ManifestSchema) (T5).
//
// KEY CUSTODY (tranchee le 11/09/2026 — la supposition d'origine « la cle privee ne vit jamais
// sur la machine » est ABANDONNEE, faute de serveur : la regle du projet est « rien hors du PC ») :
//   La cle privee est propre a la machine (MachineManifestKey) : creee par le helper eleve,
//   stockee sous %ProgramData%\DrDxt, lisible par Administrateurs + SYSTEM seulement. Seul le
//   helper eleve construit ce magasin avec un signataire (MachineManifestCommit). L'App et le
//   service reconciliateur le construisent sans (signer: null) et verifient avec la cle publique
//   posee a cote. A NULL signer makes CommitAsync fail-clean (a verify-only store cannot sign).
// -----------------------------------------------------------------------
public sealed class SignedDesiredStateStore : IDesiredStateStore, ISignedManifestReader
{
    private readonly IManifestStorage _storage;
    private readonly IManifestSigner? _signer;     // null on a verify-only (reconciler) host
    private readonly IManifestVerifier _verifier;
    private readonly IReconcilabilityPolicy _policy;
    private readonly IClock _clock;

    public SignedDesiredStateStore(
        IManifestStorage storage,
        IManifestSigner? signer,
        IManifestVerifier verifier,
        IReconcilabilityPolicy policy,
        IClock clock)
    {
        _storage = storage;
        _signer = signer;
        _verifier = verifier;
        _policy = policy;
        _clock = clock;
    }

    public async Task<Result> CommitAsync(DesiredState state)
    {
        if (_signer is null)
            return Result.Fail(ErrorCodes.OpNotAllowed, "This store is verify-only (no signing key).");

        // (1) durability scope — only OS-side reconcilable tweaks may be persisted.
        foreach (var tweak in state.Tweaks)
        {
            if (!_policy.IsReconcilable(tweak.TweakId))
                return Result.Fail(
                    ReconcilerErrorCodes.ManifestNonReconcilable,
                    $"Tweak '{tweak.TweakId}' is not an OS-side reconcilable surface.");
        }

        // (1 bis) 11/09/2026 — FUSION, jamais remplacement. Le manifeste existant est relu par le meme
        // chemin verifie ; s'il ne se relit pas (altere, illisible), on refuse d'ecrire : l'ecraser
        // effacerait les seules valeurs d'origine que la desinstallation sait rendre.
        var existant = await LoadManifestAsync();
        if (!existant.IsOk)
            return Result.Fail(existant.Error!.Value.Code,
                "The existing manifest could not be read; refusing to overwrite it (its original values "
                + $"would be lost): {existant.Error.Value.Message}");
        return await SignerEtEcrireAsync(DesiredState.Fusionner(existant.Value?.State, state));
    }

    /// <summary>06/10/2026 — retire ces identifiants des deux listes. Pas de manifeste : rien à retirer. Un manifeste
    /// illisible n'est jamais réécrit (même règle que le commit).</summary>
    public async Task<Result> RetirerAsync(IReadOnlyList<string> ids)
    {
        if (_signer is null)
            return Result.Fail(ErrorCodes.OpNotAllowed, "This store is verify-only (no signing key).");
        var existant = await LoadManifestAsync();
        if (!existant.IsOk)
            return Result.Fail(existant.Error!.Value.Code,
                $"The existing manifest could not be read; refusing to rewrite it: {existant.Error.Value.Message}");
        return existant.Value is null ? Result.Ok() : await SignerEtEcrireAsync(DesiredState.Sans(existant.Value.State, ids));
    }

    private async Task<Result> SignerEtEcrireAsync(DesiredState state)
    {
        // (2) canonical bytes + sign. 11/09/2026 : ces octets ne portent plus de duree de licence.
        // La cle qui les signe est celle de la MACHINE ; elle prouve « DrDXT a pose ceci », jamais
        // « cette machine est payee » — le proprietaire administrateur peut la lire. Le droit de
        // reappliquer se lit dans le fichier signe par l'EDITEUR (LicenceDeLaMachine).
        var now = _clock.UtcNow;
        var manifest = new DesiredStateManifest(
            DesiredStateManifest.CurrentSchemaVersion, state, now);
        var canonical = Canonicalize(manifest);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);

        var sig = _signer!.Sign(canonicalBytes); // les deux appelants ont refusé un magasin sans signataire
        if (!sig.IsOk)
            return Result.Fail(ErrorCodes.SignatureInvalid, "Failed to sign the manifest.");

        // (3) atomic persist via the admin-write storage seam.
        var envelope = new SignedManifestEnvelope(canonical, Convert.ToBase64String(sig.Value!));
        return await _storage.WriteAsync(envelope, CancellationToken.None);
    }

    public async Task<Result<DesiredState>> LoadAsync()
    {
        // Delegate to the shared verify-before-parse core (ISignedManifestReader) and map
        // to the state-only view. Behaviour is UNCHANGED: no file → empty state (first
        // install), tamper/schema → fail-clean. Aucune licence n'est consultee ici : le droit de
        // reappliquer est une decision du seul gate de l'hote, sur le fichier signe par l'editeur.
        var manifest = await LoadManifestAsync();
        if (!manifest.IsOk)
            return Result<DesiredState>.Fail(manifest.Error!.Value.Code, manifest.Error.Value.Message);

        return Result<DesiredState>.Ok(manifest.Value?.State ?? new DesiredState(Array.Empty<AppliedTweak>()));
    }

    /// <summary>Loads the FULL verified manifest (date de commit comprise). Shares the exact
    /// verify-before-parse path LoadAsync uses (the signature is the integrity authority):
    /// no file → Ok(null); tamper / bad signature / unknown schema → fail-clean.</summary>
    public async Task<Result<DesiredStateManifest?>> LoadManifestAsync()
    {
        var read = await _storage.ReadAsync(CancellationToken.None);
        if (!read.IsOk)
            return Result<DesiredStateManifest?>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        // (1) no file → no manifest (first install), not a failure.
        if (read.Value is null)
            return Result<DesiredStateManifest?>.Ok(null);

        var envelope = read.Value;

        // 26/09/2026 : un fichier « {} » ou une ecriture interrompue donne une enveloppe aux champs nuls ;
        // Convert.FromBase64String(null) levait, et le service tombait au lieu d'echouer proprement.
        if (string.IsNullOrEmpty(envelope.CanonicalJson) || string.IsNullOrEmpty(envelope.SignatureBase64))
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest envelope is incomplete.");

        // (2) VERIFY before parse. Decode the signature defensively; any error =
        // fail-closed (never fail-open).
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (FormatException)
        {
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Malformed manifest signature.");
        }

        var canonicalBytes = Encoding.UTF8.GetBytes(envelope.CanonicalJson);
        if (!_verifier.Verify(canonicalBytes, signature))
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest signature did not verify.");

        // (3) parse + schema check. Anything malformed past a VALID signature is
        // still treated fail-clean (defensive).
        DesiredStateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DesiredStateManifest>(envelope.CanonicalJson, ParseOptions);
        }
        catch (JsonException)
        {
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest body could not be parsed.");
        }

        if (manifest is null)
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest body was empty.");

        if (manifest.SchemaVersion != DesiredStateManifest.CurrentSchemaVersion)
            return Result<DesiredStateManifest?>.Fail(
                ReconcilerErrorCodes.ManifestSchema,
                $"Unsupported manifest schema version {manifest.SchemaVersion}.");

        return Result<DesiredStateManifest?>.Ok(manifest);
    }

    // -------------------------------------------------------------------
    // Canonical serialization: a DETERMINISTIC byte form so the signature is
    // computed/verified over the exact same bytes. Property order is fixed by the
    // record shape; timestamps are written as round-trip ("O") invariant strings.
    // -------------------------------------------------------------------
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private static string Canonicalize(DesiredStateManifest manifest)
        => JsonSerializer.Serialize(manifest, WriteOptions);

    /// <summary>Test hook: exposes the exact canonical form so a test can craft a
    /// correctly-signed envelope with a hand-set body (e.g. a future schema, T5).</summary>
    internal static string CanonicalizeForTest(DesiredStateManifest manifest)
        => Canonicalize(manifest);
}
