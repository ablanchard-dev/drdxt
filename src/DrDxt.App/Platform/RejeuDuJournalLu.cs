using System.Globalization;
using System.Text.Json;
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
        () => RapportDuRejeu.Lire(DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot(), RapportDuRejeu.DeConfiance),
        DossierDonnees.Chemin("rejeu-du-journal-dit.txt"));

    /// <summary>Le texte des rapports pas encore dits, ou null ; dits, ils sont retenus comme tels.</summary>
    public string? TexteNouveau()
    {
        var tous = lire();
        var dit = DejaDit();
        var nouveaux = tous.Where(r => !dit(r)).ToList();
        var jugees = nouveaux.SelectMany(r => r.Lignes).Where(l => !l.EnSuspens).ToList();
        if (jugees.Count == 0) return null;
        // Chaque rapport dit est retenu par sa date d'écriture : une date maximale cachait pour toujours un rapport écrit après
        // une horloge partie en avant puis corrigée.
        var retenu = new FichierJsonAtomique(cheminDuDejaDit, "REJEU_DIT").WriteAtomic(
            JsonSerializer.Serialize(tous.Select(r => r.Le.ToString("O", CultureInfo.InvariantCulture))));
        // Non retenu, il se redira au lancement suivant : mieux redit que perdu, et le joueur sait pourquoi.
        return retenu.IsOk ? Texte(jugees) : Texte(jugees) + Environment.NewLine + Docteur.T("JEA.NonRetenu");
    }

    /// <summary>Le texte des lignes jugées (aucune en suspens).</summary>
    internal static string Texte(IReadOnlyList<RapportDuRejeu.Ligne> jugees)
    {
        var rendues = jugees.Count(l => l.Issue == IssueDuRendu.Rendu);
        var respectees = jugees.Count(l => l.Issue == IssueDuRendu.Respecte);
        return Docteur.T("JEA.Rapport", rendues, respectees, jugees.Count - rendues - respectees) + Environment.NewLine
            + string.Join(Environment.NewLine, jugees.Select(l => $"- {l.Cible} : {l.Detail}"));
    }

    /// <summary>Les rapports déjà dits. Le fichier d'une version précédente ne porte qu'une date : tout rapport jusqu'à elle.</summary>
    private Func<RapportDuRejeu, bool> DejaDit()
    {
        var lu = new FichierJsonAtomique(cheminDuDejaDit, "REJEU_DIT").Read();
        if (!lu.IsOk || string.IsNullOrWhiteSpace(lu.Value)) return _ => false;
        if (DateTimeOffset.TryParse(lu.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var jusqua))
            return r => r.Le <= jusqua;
        try
        {
            var dits = (JsonSerializer.Deserialize<List<string>>(lu.Value) ?? [])
                .Select(s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : (DateTimeOffset?)null)
                .OfType<DateTimeOffset>().ToHashSet();
            return r => dits.Contains(r.Le);
        }
        catch (JsonException) { return _ => false; }
    }
}
