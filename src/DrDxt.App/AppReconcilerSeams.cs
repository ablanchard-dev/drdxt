using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.App;

// -----------------------------------------------------------------------
// App-side reconciler seams (M4). The host has its own copies of these in the
// service EXE (LoggingReconcilerNotifier / HostPublicKeyProvider /
// FailClosedManifestVerifier); the App cannot reference the host EXE without a bad
// dependency, so it mirrors the same behaviour over the pure reconciler types.
// Nothing here signs or holds a private key — the App surface is verify-only; the
// signing is done by the elevated helper (HelperDesiredStateStore, 11/09/2026).
// -----------------------------------------------------------------------

/// <summary>The App IReconcilerNotifier. Mirrors the host's LoggingReconcilerNotifier:
/// when a drift is classified UserIntentional/Unknown the reconciler does not overwrite
/// it (I5); it raises this notification instead. The App journals it through the same
/// IDiagnosticsLog audit trail (the structured line stays language-neutral; the
/// human-readable text resolves from a resx KEY via ILocalizer, never hardcoded — §4).
/// </summary>
public sealed class AppReconcilerNotifier : IReconcilerNotifier
{
    /// <summary>The resx key for the "a DrDXT-optimized setting was changed, left as
    /// you set it" notification. {0} = the tweak title/id. Same key the host uses.</summary>
    public const string UserDriftKey = "reconciler.notify.user_drift";

    /// <summary>Journal actor marking a "notified, not overwritten" outcome — mirrors
    /// the reconciler's own "DrDXT/notified" change-log actor.</summary>
    public const string NotifiedActor = "DrDXT/notified";

    private readonly IDiagnosticsLog _diagnostics;
    private readonly ILocalizer _localizer;

    public AppReconcilerNotifier(IDiagnosticsLog diagnostics, ILocalizer localizer)
    {
        _diagnostics = diagnostics;
        _localizer = localizer;
    }

    public Task NotifyUserIntentionalDriftAsync(
        AppliedTweak tweak, string currentValueJson, CancellationToken ct)
    {
        // Resolve the localized message from a resx key (not hardcoded). Built for the
        // audit/UI surface; the structured journal line below stays language-neutral.
        _ = string.Format(
            CultureInfo.CurrentUICulture,
            _localizer.Get(UserDriftKey),
            tweak.TweakId);

        // Journal the structured facts: the desired value drifted to the user-set value;
        // it was NOTIFIED, not overwritten (I5).
        _diagnostics.Change(
            tweakId: tweak.TweakId,
            from: tweak.DesiredValueJson,
            to: currentValueJson,
            actor: NotifiedActor);

        return Task.CompletedTask;
    }
}

// -----------------------------------------------------------------------
// AppManifestPublicKey — the App's verify-only PUBLIC-key loader.
//
// Mirrors HostPublicKeyProvider (host EXE, not referenceable here). The App VERIFIES
// the manifest to SHOW drift and never signs it: it reads the 32-byte raw Ed25519
// PUBLIC key from the same admin-write manifest root. FAIL-CLOSED: an absent/malformed
// key yields no verifier, so the verify-only store treats every manifest as
// unverifiable rather than reading an unverified one.
//
// 11/09/2026 — the public key gets ONTO the machine through the elevated helper: it is
// derived from the machine's own signing key and written next to the manifest at the first
// CommitManifestOp (MachineManifestKey). This is the consumption side only; it never loads
// a private key.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class AppManifestPublicKey
{
    private const string FileName = "manifest-public.key";
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>The default public-key path under the admin-write manifest root
    /// (sibling of the manifest + sidecar) — the same root the host reads.</summary>
    public static string DefaultPath()
        => Path.Combine(ProgramDataManifestStorage.DefaultRoot(), FileName);

    /// <summary>Builds a verify-only <see cref="IManifestVerifier"/> from the provisioned
    /// public key, or Fail if no valid key is present. Never produces a signer.</summary>
    public static Result<IManifestVerifier> LoadVerifier()
        => LoadVerifier(DefaultPath());

    /// <summary>Loads the verifier from the PROVISIONED public-key file when present
    /// (the production release-key override). With no provisioned file, a DEBUG/test
    /// build falls back to the EMBEDDED V1 public key (<see cref="ManifestDevKeys"/>) so
    /// the self-contained team build verifies real manifests; a RELEASE build does not —
    /// it FAILS CLOSED (a dev-key-signed or unsigned manifest is rejected), so a leaked
    /// dev key cannot drive the App's drift surface. A tampered / foreign-signed manifest
    /// always fails closed (the verifier holds only OUR public key).</summary>
    /// <param name="enforceTrust">When true (production), a PRESENT key file is trusted
    /// only if its owner is Administrators/SYSTEM and the root is not non-admin-writable
    /// (<see cref="TrustedManifestRoot.EnsureTrustedFile"/>); a forged key dropped into a
    /// user-owned root is rejected (BLOCKER #1a). Tests pass false to round-trip a temp
    /// file whose owner is the (non-admin) test user.</param>
    public static Result<IManifestVerifier> LoadVerifier(string path, bool enforceTrust = true)
    {
        try
        {
            if (File.Exists(path))
            {
                // At-rest integrity: a PRESENT-but-untrusted key file (user-owned / in a
                // user-writable root) is an attack, not a missing key — fail closed hard,
                // Never fall through to the embedded dev key.
                if (enforceTrust)
                {
                    var trust = TrustedManifestRoot.EnsureTrustedFile(path);
                    if (!trust.IsOk)
                        return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                            $"Manifest public key is not admin-trusted at rest: {trust.Error!.Value.Message}");
                }

                var key = File.ReadAllBytes(path);
                if (key.Length != Ed25519PublicKeyLength)
                    return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                        $"Manifest public key has wrong length ({key.Length}, expected {Ed25519PublicKeyLength}).");

                // 11/09/2026 — un fichier provisionne qui contient la cle de DEV est refuse, dans toutes les
                // configurations : sa graine privee est dans les tests du depot. Seul le repli embarque,
                // sous #if DEBUG ci-dessous, la connait encore.
                if (ManifestDevKeys.EstLaCleDeDev(key))
                    return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                        "The provisioned manifest public key is the development key; it is never trusted.");

                return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(key));
            }

#if DEBUG
            // DEBUG/test only: fall back to the embedded V1 public key so a self-contained
            // build verifies dev-key-signed test fixtures. Never in a release.
            return ManifestDevKeys.CreateVerifier();
#else
            // RELEASE: no provisioned production public key ⇒ fail-closed. The release
            // never trusts the embedded dev key (key-custody invariant, T21 gate).
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                "No manifest public key provisioned; a release build does not trust the embedded dev key (fail-closed).");
#endif
        }
        catch (Exception ex)
        {
            return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                $"Manifest public key could not be loaded: {ex.Message}");
        }
    }
}

// -----------------------------------------------------------------------
// HelperDesiredStateStore — le commit de l'app passe par le HELPER eleve (11/09/2026).
//
// Jusqu'ici l'App signait elle-meme avec une graine lue dans DRDXT_MANIFEST_SIGNING_SEED. Sur une
// machine installee cette variable n'existe pas (refus de signer) et l'App, a integrite moyenne, ne peut
// de toute facon pas ecrire la racine admin : Rien n'atteignait le manifeste. La graine en variable
// d'environnement est SUPPRIMEE du produit (SecurityGateTests : aucun code de src/ ne la lit, dans aucune
// configuration). L'App n'a plus aucune capacite de signature :
//   * CommitAsync envoie les ENTREES au helper (CommitManifestOp) : il valide, fusionne, signe avec la cle
//     de la machine et ecrit ;
//   * LoadAsync lit par un magasin VERIFY-only (cle publique de la machine, sous %ProgramData%\DrDxt) ;
//   * RafraichirAsync passe par un client qui ne lance pas le helper (pas d'UAC sans geste) ;
//   * un refus « illisible » est garde (fusionne) pour la reprise explicite, qui le renvoie avec le
//     drapeau RepartirDUnManifesteVide.
// -----------------------------------------------------------------------
public sealed class HelperDesiredStateStore : IDesiredStateStore
{
    private readonly IElevatedClient _helper;
    private readonly IElevatedClient _sansLancer;
    private readonly IDesiredStateStore _lecture;
    private readonly object _verrou = new();
    private DesiredState? _refuse;

    /// <param name="helper">Le client qui lance le helper a la demande (UAC) : gestes de l'utilisateur.</param>
    /// <param name="sansLancer">Le client qui le reutilise s'il tourne, sinon echoue vite.</param>
    /// <param name="lecture">Le magasin verify-only (aucun signataire).</param>
    public HelperDesiredStateStore(IElevatedClient helper, IElevatedClient sansLancer,
        IDesiredStateStore lecture)
    {
        _helper = helper;
        _sansLancer = sansLancer;
        _lecture = lecture;
    }

    public Task<Result<DesiredState>> LoadAsync() => _lecture.LoadAsync();

    public Task<Result> CommitAsync(DesiredState state) => EnvoyerAsync(_helper, state);

    public Task<Result> RafraichirAsync(DesiredState armed) => EnvoyerAsync(_sansLancer, armed);

    public Task<Result> RetirerAsync(IReadOnlyList<string> ids) => _helper.ExecuteAsync(
        new CommitManifestOp(Array.Empty<AppliedTweak>(), Array.Empty<AppliedTweak>(), Retirer: ids), CancellationToken.None);

    public async Task<Result> RepartirDUnManifesteVideAsync()
    {
        DesiredState aRenvoyer;
        lock (_verrou) aRenvoyer = _refuse ?? new DesiredState(Array.Empty<AppliedTweak>());

        var r = await _helper.ExecuteAsync(Op(aRenvoyer, repartir: true), CancellationToken.None).ConfigureAwait(false);
        if (r.IsOk)
            lock (_verrou)
                if (ReferenceEquals(_refuse, aRenvoyer)) _refuse = null;
        return r;
    }

    private async Task<Result> EnvoyerAsync(IElevatedClient client, DesiredState state)
    {
        var r = await client.ExecuteAsync(Op(state, repartir: false), CancellationToken.None).ConfigureAwait(false);
        if (!r.IsOk && r.Error!.Value.Code is ErrorCodes.ManifestUnreadable or ErrorCodes.ManifestKeyUnusable)
            lock (_verrou) _refuse = DesiredState.Fusionner(_refuse, state);
        return r;
    }

    private static CommitManifestOp Op(DesiredState s, bool repartir)
        => new(s.Tweaks, s.RevertOnly ?? Array.Empty<AppliedTweak>(), repartir);
}

/// <summary>A verifier that fails every check — wired only when no public key is
/// provisioned, so the App is FAIL-CLOSED (it cannot verify ⇒ it shows the manifest as
/// unverifiable and never reads an unverified one). Mirrors the host's
/// FailClosedManifestVerifier. It never throws.</summary>
internal sealed class AppFailClosedManifestVerifier : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature) => false;
}

/// <summary>11/09/2026 — le verificateur de l'App relit la cle publique a chaque verification. Elle n'existe
/// qu'apres le premier CommitManifestOp du helper : un verificateur charge une fois, au demarrage de l'App,
/// resterait « aucune cle » pour toute la session ou la cle est nee, et l'App dirait illisible le manifeste
/// qu'elle vient de faire signer. Absente ou non fiable ⇒ false, exactement comme
/// <see cref="AppFailClosedManifestVerifier"/> (et le repli DEBUG de <see cref="AppManifestPublicKey"/>
/// reste le seul endroit ou la cle de dev est connue).</summary>
[SupportedOSPlatform("windows")]
internal sealed class AppReloadingManifestVerifier(string chemin, bool enforceTrust = true) : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
    {
        var v = AppManifestPublicKey.LoadVerifier(chemin, enforceTrust);
        return v.IsOk && v.Value!.Verify(canonicalBytes, signature);
    }
}
