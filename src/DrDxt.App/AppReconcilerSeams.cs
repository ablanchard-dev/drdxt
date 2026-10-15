using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.App;

// -----------------------------------------------------------------------
// Coutures du reconciler côté App. L'hôte a ses propres copies dans l'EXE du service
// (LoggingReconcilerNotifier / HostPublicKeyProvider / FailClosedManifestVerifier) ; l'App
// ne peut pas référencer l'EXE de l'hôte sans mauvaise dépendance, elle reproduit donc le
// même comportement sur les types purs du reconciler.
// Rien ici ne signe ni ne détient de clé privée : côté App, on ne fait que vérifier ; la
// signature est faite par le helper élevé (HelperDesiredStateStore).
// -----------------------------------------------------------------------

/// <summary>L'IReconcilerNotifier de l'App, calqué sur le LoggingReconcilerNotifier de
/// l'hôte : quand une dérive est classée UserIntentional/Unknown, le reconciler ne l'écrase
/// pas (I5) et lève cette notification à la place. L'App la journalise dans la même trace
/// d'audit IDiagnosticsLog (la ligne structurée reste neutre en langue ; le texte lisible
/// vient d'une clé resx via ILocalizer, jamais écrit en dur).
/// </summary>
public sealed class AppReconcilerNotifier : IReconcilerNotifier
{
    /// <summary>La clé resx de la notification « un réglage optimisé par DrDXT a été changé,
    /// laissé tel que tu l'as mis ». {0} = le titre ou l'id du levier. Même clé que l'hôte.</summary>
    public const string UserDriftKey = "reconciler.notify.user_drift";

    /// <summary>L'acteur de journal qui marque un résultat « notifié, pas écrasé », calqué sur
    /// l'acteur « DrDXT/notified » du journal des changements du reconciler.</summary>
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
        // Le message traduit vient d'une clé resx (pas écrit en dur). Fait pour l'audit et l'UI ;
        // la ligne structurée du journal, plus bas, reste neutre en langue.
        _ = string.Format(
            CultureInfo.CurrentUICulture,
            _localizer.Get(UserDriftKey),
            tweak.TweakId);

        // Journalise les faits structurés : la valeur voulue a dérivé vers celle posée par
        // l'utilisateur ; elle a été notifiée, pas écrasée (I5).
        _diagnostics.Change(
            tweakId: tweak.TweakId,
            from: tweak.DesiredValueJson,
            to: currentValueJson,
            actor: NotifiedActor);

        return Task.CompletedTask;
    }
}

// -----------------------------------------------------------------------
// AppManifestPublicKey : le chargeur de clé publique de l'App, pour vérifier seulement.
//
// Calqué sur HostPublicKeyProvider (dans l'EXE de l'hôte, pas référençable ici). L'App
// vérifie le manifeste pour montrer la dérive et ne le signe jamais : elle lit la clé
// publique Ed25519 brute de 32 octets dans la même racine du manifeste, en écriture admin.
// Refus au moindre doute : une clé absente ou malformée ne donne aucun vérificateur, et le
// magasin en vérification seule traite alors chaque manifeste comme invérifiable au lieu
// d'en lire un non vérifié.
//
// La clé publique arrive sur la machine par le helper élevé : elle est dérivée de la clé de
// signature propre à la machine et écrite à côté du manifeste au premier CommitManifestOp
// (MachineManifestKey). Ici, on ne fait que la consommer ; aucune clé privée n'est chargée.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class AppManifestPublicKey
{
    private const string FileName = "manifest-public.key";
    private const int Ed25519PublicKeyLength = 32;

    /// <summary>Le chemin par défaut de la clé publique sous la racine du manifeste en écriture
    /// admin (à côté du manifeste et du fichier annexe) : la même racine que celle lue par l'hôte.</summary>
    public static string DefaultPath()
        => Path.Combine(ProgramDataManifestStorage.DefaultRoot(), FileName);

    /// <summary>Construit un <see cref="IManifestVerifier"/> de vérification seule à partir de la
    /// clé publique installée, ou Fail si aucune clé valide n'est là. Ne produit jamais de signataire.</summary>
    public static Result<IManifestVerifier> LoadVerifier()
        => LoadVerifier(DefaultPath());

    /// <summary>Charge le vérificateur depuis le fichier de clé publique installé, s'il existe
    /// (la clé de production qui prend le pas). Sans fichier installé, une version DEBUG ou de
    /// test se replie sur la clé publique V1 embarquée (<see cref="ManifestDevKeys"/>), pour que
    /// la version autonome de l'équipe vérifie de vrais manifestes ; une version RELEASE ne le
    /// fait pas, elle refuse (un manifeste signé par la clé de dev, ou non signé, est rejeté) :
    /// une clé de dev qui fuit ne peut pas piloter l'affichage de dérive de l'App. Un manifeste
    /// altéré ou signé par un autre est toujours refusé (le vérificateur ne détient que notre
    /// clé publique).</summary>
    /// <param name="enforceTrust">À vrai (production), un fichier de clé présent n'est cru que
    /// si son propriétaire est Administrators/SYSTEM et si la racine n'est pas inscriptible par
    /// un non-admin (<see cref="TrustedManifestRoot.EnsureTrustedFile"/>) ; une clé forgée
    /// déposée dans une racine possédée par l'utilisateur est rejetée. Les tests passent faux
    /// pour faire l'aller-retour d'un fichier temporaire dont le propriétaire est l'utilisateur
    /// de test (non admin).</param>
    public static Result<IManifestVerifier> LoadVerifier(string path, bool enforceTrust = true)
    {
        try
        {
            if (File.Exists(path))
            {
                // Intégrité au repos : un fichier de clé présent mais non fiable (possédé par l'utilisateur,
                // ou dans une racine où il peut écrire) est une attaque, pas une clé manquante : refus net,
                // jamais de repli sur la clé de dev embarquée.
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

                // Un fichier provisionne qui contient la cle de dev est refuse, dans toutes les
                // configurations : sa graine privee est dans les tests du depot. Seul le repli embarque,
                // sous #if DEBUG ci-dessous, la connait encore.
                if (ManifestDevKeys.EstLaCleDeDev(key))
                    return Result<IManifestVerifier>.Fail(ErrorCodes.SignatureInvalid,
                        "The provisioned manifest public key is the development key; it is never trusted.");

                return Result<IManifestVerifier>.Ok(new Ed25519ManifestVerifier(key));
            }

#if DEBUG
            // En DEBUG ou en test seulement : repli sur la clé publique V1 embarquée, pour qu'une
            // version autonome vérifie les fixtures de test signées par la clé de dev. Jamais en release.
            return ManifestDevKeys.CreateVerifier();
#else
            // En RELEASE : pas de clé publique de production installée ⇒ refus. La release ne fait
            // jamais confiance à la clé de dev embarquée (règle de garde des clés).
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
// HelperDesiredStateStore : le commit de l'app passe par le helper eleve.
//
// Jusqu'ici l'App signait elle-meme avec une graine lue dans DRDXT_MANIFEST_SIGNING_SEED. Sur une
// machine installee cette variable n'existe pas (refus de signer) et l'App, a integrite moyenne, ne peut
// de toute facon pas ecrire la racine admin : Rien n'atteignait le manifeste. La graine en variable
// d'environnement est supprimee du produit (SecurityGateTests : aucun code de src/ ne la lit, dans aucune
// configuration). L'App n'a plus aucune capacite de signature :
//   * CommitAsync envoie les entrees au helper (CommitManifestOp) : il valide, fusionne, signe avec la cle
//     de la machine et ecrit ;
//   * LoadAsync lit par un magasin en verification seule (cle publique de la machine, sous %ProgramData%\DrDxt) ;
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

/// <summary>Le verificateur de l'App relit la cle publique a chaque verification. Elle n'existe
/// qu'apres le premier CommitManifestOp du helper : un verificateur charge une fois, au demarrage de l'App,
/// resterait « aucune cle » pour toute la session ou la cle est nee, et l'App dirait illisible le manifeste
/// qu'elle vient de faire signer. Absente ou non fiable ⇒ false : l'App ne relit jamais un manifeste qu'elle ne
/// peut pas verifier (le repli DEBUG de <see cref="AppManifestPublicKey"/> reste le seul endroit ou la cle de
/// dev est connue).</summary>
[SupportedOSPlatform("windows")]
internal sealed class AppReloadingManifestVerifier(string chemin, bool enforceTrust = true) : IManifestVerifier
{
    public bool Verify(ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> signature)
    {
        var v = AppManifestPublicKey.LoadVerifier(chemin, enforceTrust);
        return v.IsOk && v.Value!.Verify(canonicalBytes, signature);
    }
}
