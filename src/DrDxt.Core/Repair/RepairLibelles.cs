namespace DrDxt.Core;

/// <summary>
/// Les mots affiches pour les cles d'un constat. La cle reste celle du journal (deux photos se comparent par Domaine + Nom) :
/// seul l'affichage traduit, dans la langue du docteur (Docteur.resx, cles « Domaine.* » et « Nom.* »). Descendu de l'App
/// pour que le rapport de diagnostic dise les memes mots que l'ecran.
/// </summary>
public static class RepairLibelles
{
    // Une cle inconnue s'affiche telle quelle plutot que de disparaitre.
    public static string Domaine(string cle) => Docteur.Chercher("Domaine." + cle) ?? cle;

    // Le nom est une cle du journal, ecrite sans accents ; seul l'affichage les remet. Un nom inconnu (cle BCD, service,
    // disque) s'affiche tel quel. Une majuscule initiale partout : des titres en minuscule et un nom d'element BCD brut se
    // lisaient comme un journal de debogage.
    public static string Nom(string cle) => Majuscule(Docteur.Chercher("Nom." + cle) ?? Docteur.TraduireNom(cle) ?? cle);

    // Un nom porté par deux familles (« echecs » aux services et à Windows Update) a un titre par famille, « Nom.<famille>.<nom> » ;
    // sans lui, le titre général.
    public static string Nom(string domaine, string cle) =>
        Docteur.Chercher($"Nom.{domaine}.{cle}") is { } propre ? Majuscule(propre) : Nom(cle);

    /// <summary>« Domaine · Nom », ou le nom seul quand il dit déjà son domaine : « Mises à jour Windows · mises à jour
    /// Windows en attente » ferait lire deux fois le même mot. Le titre garde le domaine sur la carte, où rien d'autre ne le dit.
    /// Seul un nom traduit fixe compte : un nom de service ou de carte peut contenir le mot par hasard.</summary>
    public static string Ligne(string domaine, string cle)
    {
        var d = Domaine(domaine);
        var n = Nom(domaine, cle);
        var fixe = Docteur.Chercher($"Nom.{domaine}.{cle}") ?? Docteur.Chercher("Nom." + cle);
        return fixe is not null && fixe.Contains(d, StringComparison.OrdinalIgnoreCase) ? n : $"{d} · {n}";
    }

    private static string Majuscule(string s)
        => string.IsNullOrEmpty(s) || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
