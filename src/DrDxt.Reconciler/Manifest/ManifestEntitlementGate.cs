using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// Le gate de reapplication.
//
// 23/09/2026 — DrDXT est GRATUIT. Jusqu'ici ce gate demandait en plus une licence signee par
// l'editeur (fichier `license-signed.json` depose par le helper) : sans elle, le reconciliateur
// refusait de reappliquer ce que la personne avait elle-meme choisi. Il n'y a plus rien a vendre,
// donc plus de droit a verifier : reste la seule question qui protege la machine.
//
// « Ce qui est arme est-il AUTHENTIQUE ? » Le manifeste est signe par la cle de la machine et
// verifie a chaque lecture. FAIL-CLOSED : manifeste non verifiable, altere ou illisible → REFUS,
// on ne reapplique jamais un etat qu'on ne sait pas relire. Aucun manifeste → Ok (no-op : une
// machine fraiche n'a rien d'arme).
// -----------------------------------------------------------------------

/// <summary>Decide si le reconciliateur peut reappliquer ce qui est arme.</summary>
public interface IEntitlementGate
{
    /// <summary>Ok ssi le manifeste arme se verifie, ou s'il n'y a aucun manifeste (rien d'arme).
    /// Fail (fail-closed) si le manifeste est illisible, altere ou non verifiable.</summary>
    Task<Result> EnsureReapplyEntitledAsync(CancellationToken ct);
}

/// <summary>Le gate de production : relit et verifie le manifeste a chaque tick. Sans etat.</summary>
public sealed class ManifestEntitlementGate : IEntitlementGate
{
    private readonly ISignedManifestReader _reader;

    public ManifestEntitlementGate(ISignedManifestReader reader)
        => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async Task<Result> EnsureReapplyEntitledAsync(CancellationToken ct)
    {
        var loaded = await _reader.LoadManifestAsync().ConfigureAwait(false);
        return loaded.IsOk ? Result.Ok() : Result.Fail(loaded.Error!.Value.Code, loaded.Error.Value.Message);
    }
}
