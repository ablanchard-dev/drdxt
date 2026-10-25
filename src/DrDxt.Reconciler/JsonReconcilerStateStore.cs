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
// Fichier absent → ReconcilerState.Default (premier lancement), pas un échec. Fichier lu mais illisible (zéros après une
// coupure, JSON tronqué, état amputé) → mis de côté (renommé, sa trace reste), dit au journal du service, et l'état repart du
// premier lancement : sinon le maintien s'arrêtait pour toujours. Seule la série de démarrages ne se sait plus ; elle repart au
// seuil du garde (BootLoopGuard), comme une série illisible, jusqu'à la prochaine session stable. Les pauses par élément ne
// sont jamais posées par DrDXT, et la pause du maintien vit dans son propre fichier. Un fichier qu'on ne peut pas lire
// maintenant (tenu, refusé) reste un échec : il n'est pas abîmé.
// L'écriture passe par FichierJsonAtomique : temporaire forcé sur le disque avant le renommage. Le helper (référence posée au
// commit) et le service (série de démarrages, comptabilité d'une passe) réécrivent ce fichier : une mise à jour lit, modifie et
// réécrit sous un verrou voisin ouvert en exclusif (MettreAJourAsync), et l'écriture de l'un ne passe jamais sous celle de l'autre.
// -----------------------------------------------------------------------
public sealed class JsonReconcilerStateStore : IReconcilerStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Func<string, bool> _fiable;
    private readonly IDiagnosticsLog? _journal;
    private readonly bool _lectureSeule;

    /// <param name="fiable">Le fichier est-il de DrDXT ? Par défaut, sous la racine partagée, propriétaire Administrateurs ou
    /// SYSTEM et aucune écriture pour un autre (<see cref="Manifest.TrustedManifestRoot.EnsureTrustedFile"/>) ; ailleurs
    /// (tests, surcharge), un brouillon cru tel quel.</param>
    /// <param name="journal">Le journal du service, où une mise de côté se dit.</param>
    /// <param name="lectureSeule">Un lecteur qui n'est pas l'écrivain (l'App, qui affiche l'état) : un état abîmé est dit
    /// illisible et laissé à sa place. Élevée, l'App l'aurait mis de côté, et le service serait reparti de l'état par défaut.</param>
    public JsonReconcilerStateStore(string path, Func<string, bool>? fiable = null, IDiagnosticsLog? journal = null,
        bool lectureSeule = false)
    {
        _path = path;
        _lectureSeule = lectureSeule;
        _journal = journal;
        _fiable = fiable ?? (p => !IsSharedTrustRoot(Path.GetDirectoryName(p) ?? "") || Manifest.TrustedManifestRoot.EnsureTrustedFile(p).IsOk);
    }

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
            // %ProgramData% laisse tout compte y poser un fichier avant l'installation, et la réparation de la racine ne descend
            // pas : un état qui n'est pas de confiance n'est pas de DrDXT. Ignoré, il vaut le premier lancement, et le prochain
            // enregistrement le remplace.
            if (!File.Exists(_path) || !_fiable(_path))
                return Task.FromResult(Result<ReconcilerState>.Ok(ReconcilerState.Default));

            var json = File.ReadAllText(_path);
            ReconcilerState? state;
            try { state = JsonSerializer.Deserialize<ReconcilerState>(json, Options); }
            catch (JsonException) { state = null; }
            // Un etat ampute se relisait sans erreur (Items nul, build nul) et le service le parcourait au demarrage : ampute =
            // illisible, mis de cote comme un JSON abime.
            if (state is null || state.LastFeatureUpdateBuild is null || state.Items is null
                || state.Items.Any(i => i?.TweakId is null))
                return Task.FromResult(MettreDeCote());
            // Un levier répété faisait lever la passe et l'amorçage de la référence (ToDictionary) : le service s'arrêtait à
            // chaque démarrage. Un seul est gardé, en pause si l'un l'est : le coupe-circuit fait faire moins, jamais plus.
            var uniques = state.Items.GroupBy(i => i.TweakId, StringComparer.Ordinal)
                .Select(g => g.FirstOrDefault(i => i.Paused) ?? g.First()).ToList();
            return Task.FromResult(Result<ReconcilerState>.Ok(uniques.Count == state.Items.Count ? state : state with { Items = uniques }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, $"State read failed: {ex.Message}"));
        }
    }

    /// <summary>Renommé, il laisse la place au premier lancement. Faux renommage : rien n'est écrit par-dessus, la lecture échoue.</summary>
    private Result<ReconcilerState> MettreDeCote()
    {
        if (_lectureSeule)
            return Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, "State read failed: unreadable state, left in place for its writer.");
        if (!new FichierJsonAtomique(_path, ErrorCodes.IoFailure).MettreDeCote())
            return Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, "State read failed: unreadable state could not be set aside.");
        _journal?.Change(Path.GetFileName(_path), "unreadable", "set-aside", "DrDXT");
        return Result<ReconcilerState>.Ok(ReconcilerState.Default with { UncleanBootStreak = BootLoopGuard.SeuilParDefaut });
    }

    /// <summary>L'attente du verrou : l'autre écrivain ne le tient que le temps d'une lecture et d'une écriture.</summary>
    private static readonly TimeSpan AttenteDuVerrou = TimeSpan.FromSeconds(10);

    public async Task<Result<ReconcilerState>> MettreAJourAsync(Func<ReconcilerState, ReconcilerState?> modifier, CancellationToken ct)
    {
        // La racine est durcie avant d'y ouvrir le verrou, comme avant d'y écrire.
        var dossier = PreparerLeDossier();
        if (!dossier.IsOk)
            return Result<ReconcilerState>.Fail(dossier.Error!.Value.Code, dossier.Error.Value.Message);

        var chemin = Path.ChangeExtension(_path, ".verrou");
        var fin = DateTime.UtcNow + AttenteDuVerrou;
        FileStream? verrou = null;
        while (verrou is null)
        {
            try { verrou = new FileStream(chemin, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < fin) { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<ReconcilerState>.Fail(ErrorCodes.IoFailure, $"State is busy: {ex.Message}");
            }
        }

        using (verrou)
        {
            var lu = await LoadAsync(ct).ConfigureAwait(false);
            if (!lu.IsOk || modifier(lu.Value!) is not { } suivant)
                return lu;
            var ecrit = await SaveAsync(suivant, ct).ConfigureAwait(false);
            return ecrit.IsOk ? Result<ReconcilerState>.Ok(suivant) : Result<ReconcilerState>.Fail(ecrit.Error!.Value.Code, ecrit.Error.Value.Message);
        }
    }

    public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct)
    {
        try
        {
            var pret = PreparerLeDossier();
            if (!pret.IsOk)
                return Task.FromResult(pret);

            var ecrit = new FichierJsonAtomique(_path, ErrorCodes.IoFailure).WriteAtomic(JsonSerializer.Serialize(state, Options));
            return Task.FromResult(ecrit.IsOk ? Result.Ok() : Result.Fail(ErrorCodes.IoFailure, $"State write failed: {ecrit.Error?.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.IoFailure, $"State write failed: {ex.Message}"));
        }
    }

    private Result PreparerLeDossier()
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
                    // vérifier / réparer) : un écrivain non élevé (par exemple l'App qui atteint l'écriture par la
                    // comptabilité du réconciliateur) ne peut ni la créer ni la réparer et échoue proprement : il ne
                    // crée jamais d'avance un %ProgramData%\DrDxt appartenant à l'utilisateur et n'écrit jamais son
                    // sidecar dans une racine squattée par l'utilisateur. Le service élevé / SYSTEM répare ici une
                    // racine squattée avant d'enregistrer le sidecar.
                    var ensured = Manifest.TrustedManifestRoot.EnsureCreatedHardened(dir);
                    if (!ensured.IsOk)
                        return ensured;
                }
                else if (!Directory.Exists(dir))
                {
                    // Un emplacement de sidecar non partagé (tests / surcharge explicite) ne porte aucune
                    // confiance : s'assurer seulement que le dossier existe.
                    Directory.CreateDirectory(dir);
                }
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, $"State write failed: {ex.Message}");
        }
    }
}
