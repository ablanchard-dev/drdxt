using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// ElevatedReapplyClient : le pont entre « un tweakId réconciliable a dérivé, le remettre à la
// valeur voulue » et le pipeline existant des ops privilégiées typées.
//
// Sécurité (I3, la leçon WinRing0) : la réapplication n'introduit jamais de nouvelle primitive
// privilégiée. Elle traduit tweakId + valeur voulue en une des feuilles PrivilegedOp scellées
// existantes (SetRegistryValueOp / SetActivePowerPlanOp / RunServiceControlOp /
// ApplyNvApiDrsSettingOp / …) par l'IReapplyOpMapper injecté, puis l'envoie par le même
// IElevatedClient.ExecuteAsync, dont elle hérite tout le chemin signature → autorisation de
// l'appelant → allowlist côté serveur. Un tweakId inconnu du mappeur échoue avant tout IPC
// (aucune op n'est jamais envoyée).
//
// Les traductions elles-mêmes (quel tweakId construit quelle feuille avec quelle valeur bornée)
// reprennent les leviers de la couche Application. Ici sont construits et testés la couture,
// l'envoi et la garantie d'échec avant IPC.
// -----------------------------------------------------------------------

/// <summary>Traduit un tweakId réconciliable + sa valeur voulue sérialisée en la feuille
/// PrivilegedOp typée existante qui le réaffirme. Un tweakId inconnu, ou une valeur voulue qui
/// ne s'analyse pas dans les champs bornés de l'op → Fail (le client de réapplication refuse
/// donc avant tout IPC). Ne rend jamais une op libre ou générique.</summary>
public interface IReapplyOpMapper
{
    Result<PrivilegedOp> Map(string tweakId, string desiredValueJson);
}

public sealed class ElevatedReapplyClient : IElevatedReapplyClient
{
    private readonly IReapplyOpMapper _mapper;
    private readonly IElevatedClient _elevated;

    public ElevatedReapplyClient(IReapplyOpMapper mapper, IElevatedClient elevated)
    {
        _mapper = mapper;
        _elevated = elevated;
    }

    public async Task<Result> ReapplyAsync(string tweakId, string desiredValueJson, CancellationToken ct)
    {
        // (1) Traduire en une op typée existante. Inconnu / non reliable → échec avant IPC.
        var op = _mapper.Map(tweakId, desiredValueJson);
        if (!op.IsOk)
            return Result.Fail(
                op.Error!.Value.Code,
                $"Cannot re-apply '{tweakId}': {op.Error.Value.Message}");

        // (2) Envoyer par le même pipeline élevé (aucune nouvelle primitive privilégiée).
        return await _elevated.ExecuteAsync(op.Value!, ct);
    }
}
