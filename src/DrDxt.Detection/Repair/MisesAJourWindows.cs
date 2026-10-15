using System.Text.RegularExpressions;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Ce que Windows Update a installé tout seul, avec son heure exacte,
/// par l'historique de l'agent (Microsoft.Update.Session, QueryHistory), en lecture seule. Relevé sur la machine de
/// référence, 40 entrées en 20 jours : les définitions de Microsoft Defender (KB2267602) plusieurs fois par jour et les
/// applications du Store (« 9NRZT3Q9R3DL-… ») à côté d'une mise à jour de pilote Realtek. Les deux premières sont
/// écartées : elles tomberaient entre toutes les parties et plus aucun changement ne se jugerait.
/// </summary>
public static class MisesAJourWindows
{
    private static readonly Regex ApplicationDuStore = new(@"^9[A-Z0-9]{11}-", RegexOptions.Compiled);

    /// <summary>Les pilotes livrés par Windows Update s'écrivent « Fournisseur - Classe - version » ou
    /// « … Driver Update (version) ».</summary>
    private static readonly Regex TitreDePilote = new(@"^.+ - .+ - [\d.]+$|Driver Update \([\d.]+\)$", RegexOptions.Compiled);

    /// <summary>La classification « Drivers » de Windows Update, la même dans toutes les langues.</summary>
    private const string ClassePilotes = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";

    /// <summary>Ce qui peut changer un jeu : ni une définition d'antivirus, ni une application du Store. Ni un pilote
    /// : setupapi le date déjà à la seconde, et deux heures différentes du même pilote se bornaient l'une l'autre,
    /// la mise à jour ne se jugeait jamais.</summary>
    internal static bool Garder(string titre, bool pilote = false)
        => !pilote && !titre.Contains("2267602", StringComparison.Ordinal) && !ApplicationDuStore.IsMatch(titre) && !TitreDePilote.IsMatch(titre);

    private static bool EstUnPilote(dynamic entree)
    {
        try
        {
            foreach (dynamic c in entree.Categories)
                if (string.Equals((string)c.CategoryID, ClassePilotes, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception) { /* entrée sans classification lisible : le titre décide */ }
        return false;
    }

    /// <summary>Installations réussies (opération 1, résultat 2), les plus récentes d'abord. Vide si l'agent ne répond pas.</summary>
    public static IReadOnlyList<FaitDate> Lire(int combien = 200)
    {
        var faits = new List<FaitDate>();
        try
        {
            if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } type) return faits;
            dynamic session = Activator.CreateInstance(type)!;
            dynamic chercheur = session.CreateUpdateSearcher();
            int total = chercheur.GetTotalHistoryCount();
            foreach (dynamic e in chercheur.QueryHistory(0, Math.Min(total, combien)))
            {
                if ((int)e.Operation != 1 || (int)e.ResultCode != 2 || e.Title is not string titre) continue;
                bool pilote = EstUnPilote(e);
                if (!Garder(titre, pilote)) continue;
                // L'agent rend l'heure en UTC.
                faits.Add(new FaitDate(new DateTimeOffset(DateTime.SpecifyKind((DateTime)e.Date, DateTimeKind.Utc)), $"Windows Update : {titre}"));
            }
        }
        catch (Exception) { /* agent absent ou refusé : rien n'est affirmé */ }
        return faits;
    }
}
