namespace DrDxt.Application.Parties;

/// <summary>Ce qui est relevé en quelques secondes avant une partie. Chaque valeur est <c>null</c> si elle n'a pas pu
/// être lue : un trou n'est jamais une gêne inventée, ni un feu vert.</summary>
public sealed record EtatAvantPartie(
    IReadOnlyList<(string Nom, double ProcesseurPourcent)>? Voisins,
    double? PingBoxMs, double? PertesBoxPourcent,
    double? PingInternetMs, double? PertesInternetPourcent,
    double? Celsius, ulong? MemoireLibreOctets);

/// <summary>
/// 04/10/2026 (plan 3b.3) : « prêt à jouer » en quelques secondes. Ce que DrDXT sait déjà mesurer, relu juste avant la
/// partie : les programmes qui chargent le processeur, la connexion (box et internet), la chaleur, la mémoire libre.
/// Les seuils sont ceux déjà fondés ailleurs dans le produit quand ils existent ; les autres sont dits NON CHIFFRÉS.
/// </summary>
public static class PretAJouer
{
    /// <summary>Un quart de cœur : le seuil du calmeur de voisins (« plus d'un quart de cœur »).</summary>
    public const double SeuilVoisinPourcent = 25;

    /// <summary>**NON CHIFFRÉ** : un aller-retour jusqu'à la box en câble tient sous 2 ms ; au-delà de 10 ms, le lien
    /// local (Wi-Fi, CPL, saturation) coûte avant même d'atteindre internet.</summary>
    public const double SeuilBoxMs = 10;

    /// <summary>1 Go : le seuil du constat de mémoire en partie (même valeur que l'outil ISLC, voir MemoireEnPartie).</summary>
    public const ulong SeuilMemoireOctets = 1024UL * 1024 * 1024;

    /// <summary>Même seuil que le ralentissement en partie (10 °C sous le freinage des Intel récents).</summary>
    public const double SeuilChaleurC = ReleveDesParties.SeuilChaleurC;

    /// <summary>Les gênes, en phrases ; vide = rien ne gêne parmi ce qui a pu être lu.</summary>
    public static IReadOnlyList<string> Genes(EtatAvantPartie e, Func<string, string> localiser)
    {
        var fr = System.Globalization.CultureInfo.CurrentCulture;
        string F(string cle, params object[] a) => string.Format(fr, localiser(cle), a);
        var genes = new List<string>();
        if (e.Voisins?.Where(v => v.ProcesseurPourcent >= SeuilVoisinPourcent).OrderByDescending(v => v.ProcesseurPourcent).Take(3).ToList()
            is { Count: > 0 } lourds)
            genes.Add(F("PretAJouer.Voisins", string.Join(", ", lourds.Select(v => $"{v.Nom} ({v.ProcesseurPourcent:0} %)"))));
        // Revue du 04/10 (P1) : une perte vers la box est un vrai problème de lien local ; vers internet, un ping isolé
        // perdu (limitation des réponses) ou filtré à 100 % (pare-feu, opérateur) n'en est pas un. Dite à partir de 2 sur 10,
        // et pas quand AUCUN ne revient alors que la box répond (filtrage, rien de mesuré).
        if (e.PertesBoxPourcent > 0 || e.PertesInternetPourcent is >= 20 and < 100)
            genes.Add(F("PretAJouer.Pertes", (e.PertesBoxPourcent ?? 0).ToString("0", fr), (e.PertesInternetPourcent ?? 0).ToString("0", fr)));
        if (e.PingBoxMs >= SeuilBoxMs)
            genes.Add(F("PretAJouer.Box", e.PingBoxMs.Value.ToString("0", fr)));
        if (e.MemoireLibreOctets < SeuilMemoireOctets)
            genes.Add(F("PretAJouer.Memoire", (e.MemoireLibreOctets.Value / 1024 / 1024).ToString("0", fr)));
        if (e.Celsius >= SeuilChaleurC)
            genes.Add(F("PretAJouer.Chaleur", e.Celsius.Value.ToString("0", fr)));
        return genes;
    }

    /// <summary>La ligne de ce qui a été mesuré, pour que « rien ne gêne » se lise avec ses chiffres.</summary>
    public static string Mesure(EtatAvantPartie e, Func<string, string> localiser)
    {
        var fr = System.Globalization.CultureInfo.CurrentCulture;
        // Vu sur la version installée : « box 0 ms » pour un aller-retour sous la milliseconde ; il se dit « < 1 ms ».
        string N(double? v, string unite) => v is { } x ? (x < 1 ? "< 1" : x.ToString("0", fr)) + unite : "?";
        return string.Format(fr, localiser("PretAJouer.Mesure"), N(e.PingBoxMs, " ms"), N(e.PingInternetMs, " ms"),
            e.MemoireLibreOctets is { } m ? (m / 1024.0 / 1024 / 1024).ToString("0.0", fr) + " Go" : "?",
            e.Celsius is { } c ? c.ToString("0", fr) + " °C" : localiser("PretAJouer.TemperatureNonLue"));
    }
}
