using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// « Annuler » et le bouton du constat de démarrage : le seul chemin où une annulation peut devenir une remise à l'usine de
/// la carte AMD. Le helper refuse de rendre une fréquence au-delà de sa bande (une sous-tension que le pilote a gardée au
/// redémarrage) et le dit par <see cref="ErrorCodes.GpuRemiseUsineRequise"/> ; sur ce clic, et seulement sur lui, la
/// remise à l'usine part. Réussie, elle vaut « rendu » : l'appelant retire l'entrée de son journal, sinon chaque « Annuler »
/// suivant la relancerait (la fréquence d'usine n'est pas forcément l'origine, chez un joueur qui avait son propre réglage).
/// Le démarrage (RevertAuDemarrage) ne passe jamais ici : il dit le refus, il n'agit pas.
/// </summary>
public static class AnnulationParClic
{
    /// <param name="Message">À montrer au joueur avec le résultat (la remise à l'usine et ce qu'elle a aussi remis), ou null.</param>
    public sealed record Issue(Result Resultat, string? Message);

    public static async Task<Issue> AnnulerAsync(ITweak levier, TweakSnapshot snapshot, IElevatedClient? helper,
        CancellationToken ct = default)
    {
        var rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);
        if (rendu.IsOk || rendu.Error?.Code != ErrorCodes.GpuRemiseUsineRequise || helper is null)
            return new Issue(rendu, null);

        var gpu = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson) is { IsOk: true, Value: ApplyAdlxTuningOp a } ? a.GpuIndex : 0;
        var remise = await helper.ExecuteAsync(new ApplyAdlxTuningOp(gpu, AdlxTuningKind.RemiseUsine, 0), ct).ConfigureAwait(false);
        return remise.IsOk
            ? new Issue(Result.Ok(), Docteur.T("ADLX.UsineRemise"))
            : new Issue(remise, null);
    }
}
