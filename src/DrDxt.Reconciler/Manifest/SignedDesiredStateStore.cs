using System.Globalization;
using System.Text;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// SignedDesiredStateStore : l'IDesiredStateStore concret (DrDxt.Core).
//
// Commit (côté qui tient la clé privée via IManifestSigner) :
//   1. Rejeter tout levier non réconciliable côté OS (firmware / inconnu / protégé) :
//      portée de durabilité.
//   2. Canonicaliser le manifeste versionné en octets déterministes.
//   3. Signer les octets canoniques ; enregistrer {canonical, signature} atomiquement par la
//      couture de stockage en écriture admin.
//
// Chargement (côté réconciliateur, qui ne tient que la clé publique via IManifestVerifier) :
//   1. Pas de fichier → DesiredState vide (première installation) : pas un échec.
//   2. Vérifier la signature sur les octets canoniques exacts enregistrés, avant l'analyse.
//      Toute altération / signature étrangère → Fail(ManifestTampered), aucun DesiredState
//      (fail-closed, jamais fail-open).
//   3. Analyser + vérifier schemaVersion ; version inconnue → Fail(ManifestSchema).
//
// Garde de la cle (la regle du projet est « rien hors du PC », donc la cle privee vit sur la machine) :
//   La cle privee est propre a la machine (MachineManifestKey) : creee par le helper eleve,
//   stockee sous %ProgramData%\DrDxt, lisible par Administrateurs + SYSTEM seulement. Seul le
//   helper eleve construit ce magasin avec un signataire (MachineManifestCommit). L'App et le
//   service reconciliateur le construisent sans (signer: null) et verifient avec la cle publique
//   posee a cote. Un signataire null fait echouer proprement CommitAsync (un magasin en verification seule ne signe pas).
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

        // (1 bis) Fusion, jamais remplacement. Le manifeste existant est relu par le meme
        // chemin verifie ; s'il ne se relit pas (altere, illisible), on refuse d'ecrire : l'ecraser
        // effacerait les seules valeurs d'origine que la desinstallation sait rendre.
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
        // (2) octets canoniques + signature. Ces octets ne portent aucune duree de licence.
        // La cle qui les signe est celle de la machine ; elle prouve « DrDXT a pose ceci », jamais
        // « cette machine est payee » : le proprietaire administrateur peut la lire. Le droit de
        // reappliquer se lit dans le fichier signe par l'editeur (LicenceDeLaMachine).
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
        // Déléguer au cœur partagé « vérifier avant d'analyser » (ISignedManifestReader) et réduire à
        // la vue de l'état seul. Pas de fichier → état vide (première installation), altération /
        // schéma → échec propre. Aucune licence : un manifeste dont la signature se vérifie se réapplique.
        var manifest = await LoadManifestAsync();
        if (!manifest.IsOk)
            return Result<DesiredState>.Fail(manifest.Error!.Value.Code, manifest.Error.Value.Message);

        return Result<DesiredState>.Ok(manifest.Value?.State ?? new DesiredState(Array.Empty<AppliedTweak>()));
    }

    /// <summary>Charge tout le manifeste vérifié (date de commit comprise). Partage exactement le
    /// chemin « vérifier avant d'analyser » de LoadAsync (la signature est l'autorité d'intégrité) :
    /// pas de fichier → Ok(null) ; altération / mauvaise signature / schéma inconnu → échec propre.</summary>
    public async Task<Result<DesiredStateManifest?>> LoadManifestAsync()
    {
        var read = await _storage.ReadAsync(CancellationToken.None);
        if (!read.IsOk)
            return Result<DesiredStateManifest?>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        // (1) pas de fichier → pas de manifeste (première installation), pas un échec.
        if (read.Value is null)
            return Result<DesiredStateManifest?>.Ok(null);

        var envelope = read.Value;

        // Un fichier « {} » ou une ecriture interrompue donne une enveloppe aux champs nuls ;
        // Convert.FromBase64String(null) levait, et le service tombait au lieu d'echouer proprement.
        if (string.IsNullOrEmpty(envelope.CanonicalJson) || string.IsNullOrEmpty(envelope.SignatureBase64))
            return Result<DesiredStateManifest?>.Fail(ErrorCodes.ManifestTampered, "Manifest envelope is incomplete.");

        // (2) Vérifier avant d'analyser. Décoder la signature par prudence ; toute erreur =
        // fail-closed (jamais fail-open).
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

        // (3) analyse + vérification du schéma. Tout ce qui est malformé derrière une signature valide
        // est encore traité en échec propre (par prudence).
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
    // Sérialisation canonique : une forme d'octets déterministe pour que la signature soit calculée
    // et vérifiée sur exactement les mêmes octets. L'ordre des propriétés est fixé par la forme du
    // record ; les horodatages s'écrivent en chaînes invariantes aller-retour (« O »).
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

    /// <summary>Accroche de test : expose la forme canonique exacte pour qu'un test fabrique une
    /// enveloppe correctement signée avec un corps posé à la main (par exemple un futur schéma).</summary>
    internal static string CanonicalizeForTest(DesiredStateManifest manifest)
        => Canonicalize(manifest);
}
