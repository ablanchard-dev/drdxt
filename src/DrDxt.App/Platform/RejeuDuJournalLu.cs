using System.Globalization;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.App.Platform;

/// <summary>
/// Les rapports du rejeu du journal d'écriture anticipée, dits une fois au joueur. Le helper et le service les écrivent à la
/// racine des données ; l'App dit ensemble tous ceux plus récents que le dernier dit, et retient chez elle la date du plus
/// récent. Les lignes en suspens attendent un rejeu suivant : elles ne se disent pas.
/// </summary>
public sealed class RejeuDuJournalLu(Func<IReadOnlyList<RapportDuRejeu>> lire, string cheminDuDejaDit)
{
    public static RejeuDuJournalLu ParDefaut() => new(
        () => RapportDuRejeu.Lire(DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot()),
        DossierDonnees.Chemin("rejeu-du-journal-dit.txt"));

    /// <summary>Le texte des rapports pas encore dits, ou null ; dits, ils sont retenus comme tels.</summary>
    public string? TexteNouveau()
    {
        var dit = DejaDit();
        var nouveaux = lire().Where(r => dit is null || r.Le > dit).ToList();
        var jugees = nouveaux.SelectMany(r => r.Lignes).Where(l => !l.EnSuspens).ToList();
        if (jugees.Count == 0) return null;
        new FichierJsonAtomique(cheminDuDejaDit, "REJEU_DIT").WriteAtomic(nouveaux.Max(r => r.Le).ToString("O", CultureInfo.InvariantCulture));
        var rendues = jugees.Count(l => l.Issue == IssueDuRendu.Rendu);
        var respectees = jugees.Count(l => l.Issue == IssueDuRendu.Respecte);
        return Docteur.T("JEA.Rapport", rendues, respectees, jugees.Count - rendues - respectees) + Environment.NewLine
            + string.Join(Environment.NewLine, jugees.Select(l => $"- {l.Cible} : {l.Detail}"));
    }

    private DateTimeOffset? DejaDit()
    {
        var lu = new FichierJsonAtomique(cheminDuDejaDit, "REJEU_DIT").Read();
        return lu.IsOk && DateTimeOffset.TryParse(lu.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }
}
