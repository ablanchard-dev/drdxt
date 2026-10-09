namespace DrDxt.Core;

/// <summary>Les constats ranges en trois piles, dans l'ordre ou un joueur veut les lire.</summary>
/// <param name="Attention">Ce qui merite son regard : un etat suspect ou nuisible, ou un geste
/// propose. C'est la seule pile qu'il doit lire en entier.</param>
/// <param name="NonLus">Ce que le produit n'a pas pu lire. Aussi important que la premiere :
/// un audit qui tait ses angles morts se fait passer pour exhaustif.</param>
/// <param name="Verifies">Ce qui a ete lu et ne demande rien. Compte, pas detaille — c'est le
/// travail fait, pas le travail a faire.</param>
public sealed record RepairTriage(
    IReadOnlyList<RepairFinding> Attention,
    IReadOnlyList<RepairFinding> NonLus,
    IReadOnlyList<RepairFinding> Verifies)
{
    /// <summary>
    /// Le tri qui decide de ce qu'un joueur voit.
    ///
    /// L'audit rendait une quarantaine de constats sur onze domaines et terminait par un
    /// decompte de statuts — « 2 recommandations, 6 Unknown ». Exact, et illisible : rien n'y
    /// disait ce qu'il faut regarder. Un rapport que personne ne lit ne vaut pas mieux qu'un
    /// rapport faux.
    ///
    /// Et la regle vit ici, pas dans l'outil, parce qu'elle DECIDE : c'est elle qui choisit ce
    /// que le produit met sous les yeux de quelqu'un. Une regle qui decide et qu'aucun test
    /// n'exerce est exactement le motif corrige deux fois le 08/09.
    ///
    /// L'ordre de priorite compte. Un constat porteur d'un GESTE va dans `Attention` meme si
    /// son statut est `Unknown` : les quatre reglages BIOS sont inconnus en valeur — on ne sait
    /// pas les lire — et pourtant chacun propose un passage guide. Les ranger dans les angles
    /// morts les rendrait invisibles.
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

    /// <summary>La phrase que le produit a le droit de dire, et rien de plus.
    ///
    /// Elle nomme les trois nombres ensemble. « Rien a signaler » sans dire combien de choses
    /// n'ont pas pu etre lues serait la meme faute que de rendre zero pour un compteur illisible.
    /// </summary>
    public string Phrase =>
        Attention.Count == 0
            ? $"rien ne demande ton attention sur les {Verifies.Count} états vérifiés"
              + (NonLus.Count > 0 ? ", mais " + NonLusDits : "")
            : ACorrigerEtARegarder
              + (Verifies.Count == 1 ? ", 1 est vérifié" : $", {Verifies.Count} sont vérifiés")
              + (NonLus.Count > 0 ? ", " + NonLusDits : "");

    // UI1 (08/10/2026) : les mots du titre. « À corriger » = nuisible ou suspect (la règle de Constat.GraviteDe, celle
    // que le titre compte) ; le reste de la pile porte un geste proposé sur un état qui n'est pas abîmé : « à regarder ».
    private string ACorrigerEtARegarder
    {
        get
        {
            var aCorriger = Attention.Count(c => c.Statut is RepairStatus.KnownHarmful or RepairStatus.Suspicious);
            var aRegarder = Attention.Count - aCorriger;
            return aCorriger == 0 ? $"{aRegarder} à regarder"
                : aRegarder == 0 ? $"{aCorriger} à corriger"
                : $"{aCorriger} à corriger · {aRegarder} à regarder";
        }
    }

    // 17/09/2026 : sur la machine de reference, un seul etat non lu, et la phrase disait
    // « 1 n'ont pas pu être lus ». Les accords se font sur le nombre, pas avec des parentheses.
    private string NonLusDits => NonLus.Count == 1 ? "1 n'a pas pu être lu" : $"{NonLus.Count} n'ont pas pu être lus";
}
