using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// ApplyReferenceSeedingStore : la reference « au moment de l'apply ». `DesiredStateReconciler.Classify` a deux signaux
// de cause non-utilisateur, gardes par des jetons du sidecar (`item.LastSeenBuild`, `item.LastSeenDriverVersion`).
// Ecrits seulement en fin de passe (`PersistBookkeepingAsync`), ils seraient morts sur un item jamais vu (test
// `PremierePasse_...`). Or l'App commite en session de bureau, le service reconcilie au demarrage et une fois par jour :
// une mise a jour de pilote ou de Windows dans cette fenetre (jusqu'a 24 h) deviendrait la reference, une derive
// notifiee a chaque passe et jamais reappliquee. Les lire avant de classifier comparerait la valeur avec elle-meme : le
// signal d'avant s'ecrit au commit qui suit l'apply.
//   * Decorateur : le manifeste est commite par deux chemins (l'apply, le rafraichissement de l'habilitation) ; le
//     seeding suit chaque commit reussi.
//   * Jamais de re-stamp : seul un champ vide est rempli, sinon le rafraichissement effacerait le signal.
//   * L'echec se dit : l'App non elevee ne peut pas ecrire la racine admin (ReferenceDeDeriveTests), le decorateur vit
//     dans le helper eleve (MachineManifestCommit). Un sidecar non ecrit ne defait pas le commit, il le signale
//     (ErrorCodes.ManifestReferenceNotSeeded) et l'ecran le montre.
// -----------------------------------------------------------------------
public sealed class ApplyReferenceSeedingStore : IDesiredStateStore
{
    private readonly IDesiredStateStore _inner;
    private readonly IReconcilerStateStore _sidecar;
    private readonly IOsBuildProbe _buildProbe;
    private readonly IGpuDriverProbe _driverProbe;
    private readonly IReconcilabilityPolicy _policy;

    public ApplyReferenceSeedingStore(
        IDesiredStateStore inner,
        IReconcilerStateStore sidecar,
        IOsBuildProbe buildProbe,
        IGpuDriverProbe driverProbe,
        IReconcilabilityPolicy policy)
    {
        _inner = inner;
        _sidecar = sidecar;
        _buildProbe = buildProbe;
        _driverProbe = driverProbe;
        _policy = policy;
    }

    public Task<Result<DesiredState>> LoadAsync() => _inner.LoadAsync();

    public Task<Result> RetirerAsync(IReadOnlyList<string> ids) => _inner.RetirerAsync(ids);

    // Simplification voulue : transmise sans seeding ; le reconciliateur pose ces jetons a sa premiere passe.
    public Task<Result> RepartirDUnManifesteVideAsync() => _inner.RepartirDUnManifesteVideAsync();

    // Transmis tel quel : un rafraichissement ne pose rien de neuf, et garde le client de l'appele (celui de
    // HelperDesiredStateStore ne lance jamais l'UAC) ; la methode par defaut repasserait par CommitAsync.
    public Task<Result> RafraichirAsync(DesiredState armed) => _inner.RafraichirAsync(armed);

    public async Task<Result> CommitAsync(DesiredState state)
    {
        var commit = await _inner.CommitAsync(state);
        if (!commit.IsOk)
            return commit; // rien n'est arme ⇒ rien a referencer.

        Result reference;
        try
        {
            reference = await SeedAsync(state);
        }
        catch (Exception ex)
        {
            reference = Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }

        // Le manifeste est ecrit ; seul le repere manque. Le reconciliateur le posera a sa premiere passe
        // (une passe plus tard : il notifiera une derive au lieu de la reappliquer). Dit, jamais tu.
        return reference.IsOk
            ? commit
            : Result.Fail(ErrorCodes.ManifestReferenceNotSeeded,
                "The signed manifest was written, but the drift reference (Windows build, driver version) could "
                + $"not be recorded: {reference.Error?.Message} The reconciler's first pass will notify instead of re-applying.");
    }

    private async Task<Result> SeedAsync(DesiredState state)
    {
        if (state.Tweaks.Count == 0)
            return Result.Ok(); // un keep (RevertOnly) n'est jamais reaffirme : rien a referencer.

        var build = _buildProbe.ReadCurrentBuild();
        var buildToken = build.IsOk ? build.Value! : string.Empty;

        // Une lecture pilote (WMI) par vendeur, memorisee ; aucune pour un item sans vendeur.
        var versionsParVendeur = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Relire, poser, reecrire d'un seul tenant : le service reecrit ce fichier a chaque passe.
        var r = await _sidecar.MettreAJourAsync(current => AvecLesReferences(current, state, buildToken, versionsParVendeur),
            CancellationToken.None);
        return r.IsOk ? Result.Ok() : Result.Fail(r.Error!.Value.Code, r.Error.Value.Message);
    }

    private ReconcilerState? AvecLesReferences(ReconcilerState current, DesiredState state, string buildToken,
        Dictionary<string, string?> versionsParVendeur)
    {
        var byId = current.Items.ToDictionary(i => i.TweakId, StringComparer.Ordinal);

        var change = false;
        foreach (var tweak in state.Tweaks)
        {
            byId.TryGetValue(tweak.TweakId, out var prev);

            var manqueBuild = string.IsNullOrEmpty(prev?.LastSeenBuild);
            var manquePilote = string.IsNullOrEmpty(prev?.LastSeenDriverVersion);
            if (!manqueBuild && !manquePilote)
                continue; // une reference existe deja : on n'y touche pas.

            string? versionPilote = null;
            var vendeur = manquePilote ? _policy.VendorFor(tweak.TweakId) : null;
            if (vendeur is not null && !versionsParVendeur.TryGetValue(vendeur, out versionPilote))
            {
                var lu = _driverProbe.ReadDriverVersion(vendeur);
                versionPilote = lu.IsOk ? lu.Value : null;
                versionsParVendeur[vendeur] = versionPilote;
            }

            var nouveauBuild = manqueBuild ? buildToken : prev!.LastSeenBuild;
            var nouveauPilote = manquePilote ? versionPilote ?? string.Empty : prev!.LastSeenDriverVersion;
            if (nouveauBuild.Length == 0 && nouveauPilote.Length == 0)
                continue; // les deux sondes muettes : rien de vrai a poser.

            byId[tweak.TweakId] = new ReconcilerItemState(
                TweakId: tweak.TweakId,
                Paused: prev?.Paused ?? false,     // le kill-switch n'est jamais efface.
                LastSeenBuild: nouveauBuild,
                LastSeenDriverVersion: nouveauPilote,
                // Aucune passe n'a eu lieu : pas d'horodatage d'un « reconcilie » inexistant.
                LastReconciledAt: prev?.LastReconciledAt ?? default,
                LastResultCode: prev?.LastResultCode ?? string.Empty);
            change = true;
        }

        return change ? current with { Items = byId.Values.ToArray() } : null;
    }
}
