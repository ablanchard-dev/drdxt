namespace DrDxt.Application.Parties;

/// <summary>Un fait daté de la frise : un changement, une partie, un arrêt brutal, une erreur matérielle.</summary>
public sealed record EvenementDeFrise(DateTimeOffset Quand, GenreDEvenement Genre, string Texte)
{
    /// <summary>Ce qui s'aligne à droite (les FPS d'une partie) ; vide sinon.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Le nom que le lecteur d'écran annonce pour l'élément de liste : WPF lit ToString quand l'élément n'a pas de nom.</summary>
    public override string ToString()
        => $"{Quand.ToLocalTime().ToString("g", DrDxt.Core.Docteur.Langue)} {Texte}" + (Detail.Length > 0 ? $", {Detail}" : "");
}

public enum GenreDEvenement { Changement, Partie, ArretBrutal, ErreurMaterielle }

/// <summary>
/// L'historique de la machine sur une seule ligne du temps. Chaque fait existait déjà, éparpillé :
/// les changements au journal, les parties à leur carte, les arrêts et les erreurs dans le docteur. Côte à côte, on lit
/// d'un coup d'œil « j'ai changé ça, puis mes parties ont fait ça, puis le PC s'est coupé ». Pur : tout vient de l'appelant.
/// </summary>
public static class FriseDeLaMachine
{
    public const int Jours = 30;
    public const int Lignes = 15;

    public static IReadOnlyList<EvenementDeFrise> Construire(IReadOnlyList<Changement> changements, IReadOnlyList<PartieRelevee> parties,
        IReadOnlyList<DateTimeOffset> arrets, IReadOnlyList<DateTimeOffset> erreurs, DateTimeOffset maintenant,
        Func<Changement, string> texteChangement, Func<PartieRelevee, string> textePartie, string texteArret, string texteErreur,
        Func<PartieRelevee, string>? detailPartie = null)
    {
        var depuis = maintenant.AddDays(-Jours);
        return changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true)
                .Select(c => new EvenementDeFrise(c.Quand, GenreDEvenement.Changement, texteChangement(c)))
            .Concat(parties.Select(p => new EvenementDeFrise(p.Fin, GenreDEvenement.Partie, textePartie(p)) { Detail = detailPartie?.Invoke(p) ?? "" }))
            .Concat(arrets.Select(a => new EvenementDeFrise(a, GenreDEvenement.ArretBrutal, texteArret)))
            .Concat(erreurs.Select(e => new EvenementDeFrise(e, GenreDEvenement.ErreurMaterielle, texteErreur)))
            .Where(e => e.Quand >= depuis && e.Quand <= maintenant.AddMinutes(5))
            .OrderByDescending(e => e.Quand)
            .Take(Lignes)
            .ToList();
    }
}
