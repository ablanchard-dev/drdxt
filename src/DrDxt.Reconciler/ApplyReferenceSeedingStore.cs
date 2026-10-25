using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// ApplyReferenceSeedingStore : la reference « au moment de l'apply ».
//
// Le trou qu'elle ferme. `DesiredStateReconciler.Classify` a deux signaux de cause
// non-utilisateur, tous deux gardes par un jeton du sidecar : `item.LastSeenBuild` et
// `item.LastSeenDriverVersion`. Si ces jetons n'etaient ecrits que par
// `PersistBookkeepingAsync` (fin d'une passe du reconciliateur), les deux branches seraient
// mortes sur un item jamais vu (`item is null`, test `PremierePasse_...`) : la premiere passe
// ne pourrait classer que `UserIntentional` (valeur tierce) ou `Unknown` (revenu a
// l'original), donc notifier.
//
// Le scenario n'a rien de theorique : l'App applique et commite le manifeste pendant la
// session de bureau, alors que le service reconcilie au demarrage et une fois par jour.
// Entre les deux il y a une fenetre reelle : un redemarrage, jusqu'a 24 h, ou le service
// simplement arrete. Une mise a jour de pilote ou de fonctionnalite Windows qui tombe dans
// cette fenetre remet le reglage a son etat d'origine ; la premiere passe enregistrerait
// alors ce nouvel etat comme reference, et la derive resterait notifiee a chaque passe,
// jamais reappliquee.
//
// Lire les jetons avant de classifier au lieu d'apres ne repare rien : cela donne l'etat
// courant, celui d'apres le changement, donc une comparaison de la valeur avec elle-meme.
// Le signal qui manque est celui d'avant : il faut l'ecrire la ou on sait encore ce qui
// etait vrai, au commit qui suit l'apply. C'est tout ce que fait cette classe.
//
// Pourquoi un decorateur, et pas un appel dans le viewmodel : le manifeste est commite par
// deux chemins (l'apply, et le rafraichissement periodique de l'habilitation). Le seeding
// doit suivre le commit, jamais l'oublier, et ne s'executer qu'apres un commit reussi.
//
// Il ne re-stampe jamais un jeton deja pose. Sans cette regle, le rafraichissement
// periodique remettrait la reference a « maintenant » a chaque tour et effacerait
// exactement le signal qu'on cherche a armer. Seul un champ vide est rempli.
//
// L'echec se dit. Dans l'App non elevee, qui ne peut pas ecrire la racine admin, la mise a jour
// du sidecar rendrait Fail et le commit Ok (ReferenceDeDeriveTests) : le decorateur vit donc dans le
// helper eleve (MachineManifestCommit). Le manifeste reste l'autorite : un sidecar illisible ou
// non ecrit ne defait pas le commit, mais il le signale (ErrorCodes.ManifestReferenceNotSeeded),
// et l'ecran le montre.
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

    // Transmis tel quel : un rafraichissement ne pose rien de neuf, et il garde le client de l'appele
    // (celui de HelperDesiredStateStore ne lance jamais l'UAC). Sans cette ligne, la methode par defaut de
    // l'interface repassait par CommitAsync, donc par le client qui lance le helper.
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

        // Une lecture pilote par vendeur, memorisee : un item sans vendeur n'en declenche
        // aucune, dix items NVIDIA n'en declenchent qu'une (la sonde reelle interroge WMI).
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
                // Aucune passe n'a eu lieu : le dire, plutot que d'horodater un
                // « reconcilie » qui n'a pas existe.
                LastReconciledAt: prev?.LastReconciledAt ?? default,
                LastResultCode: prev?.LastResultCode ?? string.Empty);
            change = true;
        }

        return change ? current with { Items = byId.Values.ToArray() } : null;
    }
}
