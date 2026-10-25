namespace DrDxt.Core;

// --- Maintenance ---

/// <param name="PoseesJson">Ce que DrDXT a pose, feuille par feuille, quand le levier en a
/// plusieurs. <paramref name="DesiredValueJson"/> ne porte qu'une valeur (celle que le reconciliateur
/// reaffirme au demarrage) : pour un levier composite, les autres feuilles n'avaient aucune valeur posee
/// connue, donc la desinstallation ne pouvait jamais les juger rendables, et avec le tout-ou-rien
/// le composite entier restait bloque pour toujours. <c>null</c> = levier a une seule feuille, ou
/// manifeste ecrit avant ce champ : la valeur posee reste deduite de <paramref name="DesiredValueJson"/>.</param>
public record AppliedTweak(
    string TweakId,
    string DesiredValueJson,
    string OriginalValueJson,
    DateTimeOffset AppliedAt,
    string? PoseesJson = null);

/// <param name="Tweaks">Ce que le reconciliateur reaffirme au demarrage (corrections au redemarrage).</param>
/// <param name="RevertOnly">Des valeurs d'origine que seule la desinstallation rend : les
/// leviers gardes en session. Le reconciliateur ne lit que <paramref name="Tweaks"/>, il ne peut donc
/// pas les reaffirmer par construction : un keep « trouve en decouverte, a revalider » ne devient pas
/// durable par la bande. <c>null</c> = manifeste ecrit avant ce champ.</param>
public record DesiredState(IReadOnlyList<AppliedTweak> Tweaks, IReadOnlyList<AppliedTweak>? RevertOnly = null)
{
    /// <summary>
    /// Le manifeste fusionne, il ne se remplace plus. Le commit d'un clic ecrasait celui du
    /// clic d'avant : une correction armee au 1er clic disparaissait au 2e, et la desinstallation ne la
    /// rendait plus (prouve par `ManifesteFusionTests`). Par identifiant : un levier nouveau s'ajoute, un
    /// levier connu met a jour sa valeur voulue et garde sa premiere origine : un levier retente capture
    /// l'etat deja change comme « origine », et l'ecrire perdrait la vraie. La fusion ne retire rien ; seul
    /// <see cref="Sans"/> retire (levier annule).
    /// </summary>
    public static DesiredState Fusionner(DesiredState? existant, DesiredState ajout) => new(
        FusionnerListe(existant?.Tweaks, ajout.Tweaks),
        existant?.RevertOnly is null && ajout.RevertOnly is null
            ? null
            : FusionnerListe(existant?.RevertOnly, ajout.RevertOnly));

    /// <summary>La seule voie qui retire : un levier annulé quitte les deux listes. Sans
    /// elle, le service reposait au démarrage un levier annulé, et sa première origine survivait à une nouvelle pose.</summary>
    public static DesiredState Sans(DesiredState etat, IReadOnlyCollection<string> ids) => new(
        etat.Tweaks.Where(t => !ids.Contains(t.TweakId, StringComparer.Ordinal)).ToList(),
        etat.RevertOnly?.Where(t => !ids.Contains(t.TweakId, StringComparer.Ordinal)).ToList());

    private static IReadOnlyList<AppliedTweak> FusionnerListe(
        IReadOnlyList<AppliedTweak>? avant, IReadOnlyList<AppliedTweak>? ajout)
    {
        var sortie = (avant ?? Array.Empty<AppliedTweak>()).ToList();
        foreach (var t in ajout ?? Array.Empty<AppliedTweak>())
        {
            var i = sortie.FindIndex(x => string.Equals(x.TweakId, t.TweakId, StringComparison.Ordinal));
            if (i < 0) sortie.Add(t);
            else sortie[i] = t with { OriginalValueJson = sortie[i].OriginalValueJson };
        }
        return sortie;
    }
}

public enum DriftCause { WindowsReset, UserIntentional, Unknown }

public interface IReconciler
{
    Task<Result> ReconcileAsync(DesiredState desired, CancellationToken ct); // idempotent
    Task<Result<DriftCause>> ClassifyDriftAsync(AppliedTweak tweak);
}

public interface IDesiredStateStore // l'application écrit (op signée), le reconciler lit
{
    /// <summary>Fusionne <paramref name="state"/> dans le manifeste (<see cref="DesiredState.Fusionner"/>) :
    /// jamais un remplacement, et un manifeste existant illisible n'est jamais ecrase.</summary>
    Task<Result> CommitAsync(DesiredState state);
    Task<Result<DesiredState>> LoadAsync();

    /// <summary>Re-signer l'etat deja arme (habilitation) sans jamais declencher d'elevation :
    /// un rafraichissement de fond qui ferait surgir l'UAC sans geste de l'utilisateur est exactement ce
    /// qui detruit la confiance. Par defaut : un commit ordinaire.</summary>
    Task<Result> RafraichirAsync(DesiredState armed) => CommitAsync(armed);

    Task<Result> RetirerAsync(IReadOnlyList<string> ids)
        => Task.FromResult(Result.Fail(ErrorCodes.OpNotAllowed, "This store cannot remove an entry."));

    /// <summary>La reprise, sur geste explicite de l'utilisateur, quand un commit a ete
    /// refuse parce que le manifeste existant est illisible (ou la cle inutilisable) : l'ancien est mis de
    /// cote (jamais efface) et un manifeste neuf commence avec ce qui avait ete refuse. Par defaut : refus,
    /// un magasin sans voie de reprise le dit.</summary>
    Task<Result> RepartirDUnManifesteVideAsync()
        => Task.FromResult(Result.Fail(ErrorCodes.OpNotAllowed, "This store has no recovery path."));
}
