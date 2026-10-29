using System.Globalization;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// SignedDesiredStateStore : l'IDesiredStateStore concret (DrDxt.Core).
//
// Commit (avec la clé privée, IManifestSigner) :
//   1. rejeter tout levier non réconciliable côté OS (firmware, inconnu, protégé) ;
//   2. canonicaliser le manifeste versionné en octets déterministes ;
//   3. signer ; enregistrer {canonical, signature} atomiquement dans le stockage en écriture admin.
// Chargement (réconciliateur, clé publique seule, IManifestVerifier) :
//   1. pas de fichier → DesiredState vide (première installation), pas un échec ;
//   2. signature vérifiée sur les octets exacts avant l'analyse ; altération ou signature étrangère →
//      Fail(ManifestTampered), aucun DesiredState (fail-closed) ;
//   3. schemaVersion inconnue → Fail(ManifestSchema).
// Garde de la cle (« rien hors du PC ») : la cle privee de la machine (MachineManifestKey), creee par le helper eleve
// sous %ProgramData%\DrDxt, lisible par Administrateurs + SYSTEM seulement. Seul le helper construit ce magasin avec un
// signataire (MachineManifestCommit) ; l'App et le service sans (signer: null), et CommitAsync y echoue proprement.
// -----------------------------------------------------------------------
public sealed class SignedDesiredStateStore : IDesiredStateStore, ISignedManifestReader
{
    private readonly IManifestStorage _storage;
    private readonly IManifestSigner? _signer;     // null sur un hôte en vérification seule (réconciliateur)
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

        // (1) portée de durabilité : seuls les leviers réconciliables côté OS peuvent être enregistrés.
        foreach (var tweak in state.Tweaks)
        {
            if (!_policy.IsReconcilable(tweak.TweakId))
                return Result.Fail(
                    ReconcilerErrorCodes.ManifestNonReconcilable,
                    $"Tweak '{tweak.TweakId}' is not an OS-side reconcilable surface.");
        }

        // (1 bis) Fusion, jamais remplacement : l'existant relu par le chemin verifie ; illisible, rien n'est ecrit (ce sont
        // les seules origines que la desinstallation sait rendre).
        var existant = await LoadManifestAsync();
        if (!existant.IsOk)
            return Result.Fail(existant.Error!.Value.Code,
                "The existing manifest could not be read; refusing to overwrite it (its original values "
                + $"would be lost): {existant.Error.Value.Message}");
        return await SignerEtEcrireAsync(DesiredState.Fusionner(existant.Value?.State, state));
    }

    /// <summary>Retire ces identifiants des deux listes. Pas de manifeste : rien à retirer. Un manifeste
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
        // (2) octets canoniques + signature par la cle de la machine : une preuve face a un programme non eleve, pas face a
        // un administrateur, qui peut la lire.
        var now = _clock.UtcNow;
        var manifest = new DesiredStateManifest(
            DesiredStateManifest.CurrentSchemaVersion, state, now);
        var canonical = Canonicalize(manifest);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);

        var sig = _signer!.Sign(canonicalBytes); // les deux appelants ont refusé un magasin sans signataire
        if (!sig.IsOk)
            return Result.Fail(ErrorCodes.SignatureInvalid, "Failed to sign the manifest.");

        // (3) enregistrement atomique par la couture de stockage en écriture admin.
        var envelope = new SignedManifestEnvelope(canonical, Convert.ToBase64String(sig.Value!));
        return await _storage.WriteAsync(envelope, CancellationToken.None);
    }

    public async Task<Result<DesiredState>> LoadAsync()
    {
        // Le cœur « vérifier avant d'analyser » (ISignedManifestReader), réduit à l'état : pas de fichier → vide,
        // altération ou schéma → échec propre.
        var manifest = await LoadManifestAsync();
        if (!manifest.IsOk)
            return Result<DesiredState>.Fail(manifest.Error!.Value.Code, manifest.Error.Value.Message);

        return Result<DesiredState>.Ok(manifest.Value?.State ?? new DesiredState(Array.Empty<AppliedTweak>()));
    }

    /// <summary>Tout le manifeste vérifié (date de commit comprise), par le chemin « vérifier avant d'analyser » : pas de
    /// fichier → Ok(null) ; altération, mauvaise signature, schéma inconnu → échec propre.</summary>
    public async Task<Result<DesiredStateManifest?>> LoadManifestAsync()
    {
        var read = await _storage.ReadAsync(CancellationToken.None);
        if (!read.IsOk)
            return Result<DesiredStateManifest?>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        // (1) pas de fichier → pas de manifeste (première installation), pas un échec.
        if (read.Value is null)
            return Result<DesiredStateManifest?>.Ok(null);

        var envelope = read.Value;

        // « {} » ou une ecriture interrompue : enveloppe aux champs nuls, que Convert.FromBase64String(null) ferait lever.
        if (string.IsNullOrEmpty(envelope.CanonicalJson) || string.IsNullOrEmpty(envelope.SignatureBase64))
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest envelope is incomplete.");

        // (2) Vérifier avant d'analyser ; toute erreur de décodage = fail-closed.
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

        // (3) analyse + schéma ; malformé derrière une signature valide = échec propre.
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
    // Sérialisation canonique : les mêmes octets signés et vérifiés ; ordre fixé par le record, horodatages en « O ».
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

    /// <summary>Accroche de test : la forme canonique, pour signer un corps posé à la main (un futur schéma).</summary>
    internal static string CanonicalizeForTest(DesiredStateManifest manifest)
        => Canonicalize(manifest);
}
