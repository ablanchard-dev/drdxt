namespace DrDxt.Core;

/// <summary>Les constats ranges en trois piles, dans l'ordre ou un joueur veut les lire.</summary>
/// <param name="Attention">Ce qui merite son regard : un etat suspect ou nuisible, ou un geste
/// propose. C'est la seule pile qu'il doit lire en entier.</param>
/// <param name="NonLus">Ce que le produit n'a pas pu lire. Aussi important que la premiere :
/// un audit qui tait ses angles morts se fait passer pour exhaustif.</param>
/// <param name="Verifies">Ce qui a ete lu et ne demande rien. Compte, pas detaille : c'est le
/// travail fait, pas le travail a faire.</param>
public sealed record RepairTriage(
    IReadOnlyList<RepairFinding> Attention,
    IReadOnlyList<RepairFinding> NonLus,
    IReadOnlyList<RepairFinding> Verifies)
{
    /// <summary>
    /// Le tri qui decide de ce qu'un joueur voit, parmi une quarantaine de constats sur onze domaines (un decompte de
    /// statuts, « 2 recommandations, 6 Unknown », serait exact et illisible). Ici, et pas dans l'outil, pour etre exerce
    /// par un test. Un constat porteur d'un geste va dans `Attention` meme `Unknown` : les quatre reglages BIOS sont
    /// illisibles en valeur et proposent chacun un passage guide.
    /// </summary>
    public static RepairTriage Trier(IEnumerable<RepairFinding> constats)
    {
        var attention = new List<RepairFinding>();
        var nonLus = new List<RepairFinding>();
        var verifies = new List<RepairFinding>();

        foreach (var c in constats ?? [])
        {
            if (c.Statut is RepairStatus.KnownHarmful or RepairStatus.Suspicious
                || c.Action != RepairAction.Aucune)
                attention.Add(c);
            else if (c.Statut == RepairStatus.Unknown)
                nonLus.Add(c);
            else
                verifies.Add(c);
        }

        return new RepairTriage(attention, nonLus, verifies);
    }

    /// <summary>La phrase que le produit a le droit de dire : les trois nombres ensemble, jamais « rien a signaler » sans
    /// le compte des non-lus.</summary>
    public string Phrase =>
        Attention.Count == 0
            // Le compte s'accorde ; zéro état vérifié ne se dit pas « rien ne demande ton attention ».
            ? (Verifies.Count switch { 0 => Docteur.T("RT.RienAucun"), 1 => Docteur.T("RT.RienUn"), _ => Docteur.T("RT.Rien", Verifies.Count) })
              + (NonLus.Count > 0 ? Docteur.T("RT.Mais") + NonLusDits : "")
            : ACorrigerEtARegarder
              + (Verifies.Count == 1 ? Docteur.T("RT.UnVerifie") : Docteur.T("RT.Verifies", Verifies.Count))
              + (NonLus.Count > 0 ? ", " + NonLusDits : "");

    // Les mots du titre. « À corriger » = nuisible ou suspect (la règle de Constat.GraviteDe, celle
    // que le titre compte) ; le reste de la pile porte un geste proposé sur un état qui n'est pas abîmé : « à regarder ».
    private string ACorrigerEtARegarder
    {
        get
        {
            var aCorriger = Attention.Count(c => c.Statut is RepairStatus.KnownHarmful or RepairStatus.Suspicious);
            var aRegarder = Attention.Count - aCorriger;
            return aCorriger == 0 ? Docteur.T("RT.ARegarder", aRegarder)
                : aRegarder == 0 ? Docteur.T("RT.ACorriger", aCorriger)
                : Docteur.T("RT.LesDeux", aCorriger, aRegarder);
        }
    }

    // Accord sur le nombre, pas de parentheses : jamais « 1 n'ont pas pu être lus ».
    private string NonLusDits => NonLus.Count == 1 ? Docteur.T("RT.UnNonLu") : Docteur.T("RT.NonLus", NonLus.Count);
}
