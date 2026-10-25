using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>
/// Ce que le tube fait exécuter passe après un rejeu en cours. Le rejeu rend les origines qu'une App morte a laissées ; l'App
/// relancée, connectée au même instant, pouvait poser le même réglage avant que l'origine soit revenue, et le journal notait
/// alors la valeur de DrDXT comme image d'avant. Le rejeu passe par l'exécuteur intérieur : il n'attend jamais lui-même.
/// Une lecture n'écrit rien et n'attend pas.
/// </summary>
public sealed class ExecuteurApresLeRejeu(IPrivilegedOpExecutor interieur, RejeuEnSerie rejeu) : IPrivilegedOpExecutor
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => rejeu.HorsRejeuAsync(() => interieur.ExecuteAsync(op, ct), ct);

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct, DrDxt.ElevatedHelper.Ipc.CallerInfo appelant)
        => rejeu.HorsRejeuAsync(() => interieur.ExecuteAsync(op, ct, appelant), ct);

    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interieur.ReadAsync(op, ct);
}
