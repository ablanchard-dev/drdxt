using DrDxt.Application.Tweaks;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.App.Platform;

/// <summary>
/// Le rejeu du journal d'écriture anticipée rend une pose dont l'App n'a pas acquitté l'origine : l'App tuée entre la note de
/// l'origine et l'acquit, ou un acquit refusé jusqu'à la fermeture. Si l'origine est déjà notée, le levier reste au journal
/// des leviers, et le scan suivant le dirait « défait » (par une mise à jour de Windows). Un levier dont une feuille a été rendue
/// par un rejeu, et qui se relit à son origine, n'est plus gardé. Relu encore posé (reposé depuis), il le reste.
/// </summary>
public static class LeviersRendusParLeRejeu
{
    /// <summary>Les leviers du journal rendus par un rejeu (lignes « rendu » qui portent une clé), relus à leur origine.</summary>
    public static IReadOnlyList<string> Ids(IReadOnlyList<TweakSnapshot> journal, IEnumerable<RapportDuRejeu.Ligne> lignes,
        Func<TweakSnapshot, EtatApplique> relu, string sid)
    {
        var rendues = lignes.Where(l => l.Issue == IssueDuRendu.Rendu && l.Cle is not null).Select(l => l.Cle!).ToHashSet(StringComparer.Ordinal);
        if (rendues.Count == 0) return [];
        return journal.Where(s => Posees(s).Any(op => rendues.Contains(RejeuDuJournal.CleDe(op, sid))) && relu(s) == EtatApplique.NotApplied)
            .Select(s => s.TweakId).Distinct().ToList();
    }

    private static IReadOnlyList<PrivilegedOp> Posees(TweakSnapshot s)
    {
        if (s.AppliedValueJson is not { Length: > 0 } json) return [];
        if (CompositeSnapshotCodec.Decode(json) is { IsOk: true, Value: { Count: > 0 } composite }) return composite;
        return PrivilegedOpCodec.Deserialize(json) is { IsOk: true, Value: { } op } ? [op] : [];
    }
}
