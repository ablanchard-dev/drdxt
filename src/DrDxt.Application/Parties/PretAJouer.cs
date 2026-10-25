namespace DrDxt.Application.Parties;

/// <summary>Ce qui est relevé en quelques secondes avant une partie. Chaque valeur est <c>null</c> si elle n'a pas pu
/// être lue : un trou n'est jamais une gêne inventée, ni un feu vert.</summary>
public sealed record EtatAvantPartie(
    IReadOnlyList<(string Nom, double ProcesseurPourcent)>? Voisins,
    double? PingBoxMs, double? PertesBoxPourcent,
    double? PingInternetMs, double? PertesInternetPourcent,
    double? Celsius, ulong? MemoireLibreOctets,
    DateTimeOffset? DernierDemarrage = null, IReadOnlyList<(string Nom, long Octets)>? MemoireVideo = null,
    IReadOnlyList<string>? AudioVirtuel = null, bool? ConversionDuDisque = null);

/// <summary>
/// « prêt à jouer » en quelques secondes. Ce que DrDXT sait déjà mesurer, relu juste avant la
/// partie : les programmes qui chargent le processeur, la connexion (box et internet), la chaleur, la mémoire libre.
/// Les seuils sont ceux déjà fondés ailleurs dans le produit quand ils existent ; les autres sont dits non chiffrés.
/// </summary>
public static class PretAJouer
{
    /// <summary>Un quart de cœur : le seuil du calmeur de voisins (« plus d'un quart de cœur »).</summary>
    public const double SeuilVoisinPourcent = 25;

    /// <summary>**Non chiffré** : un aller-retour jusqu'à la box en câble tient sous 2 ms ; au-delà de 10 ms, le lien
    /// local (Wi-Fi, CPL, saturation) coûte avant même d'atteindre internet.</summary>
    public const double SeuilBoxMs = 10;

    /// <summary>1 Go : le seuil du constat de mémoire en partie (même valeur que l'outil ISLC, voir MemoireEnPartie).</summary>
    public const ulong SeuilMemoireOctets = 1024UL * 1024 * 1024;

    /// <summary>Même seuil que le ralentissement en partie (10 °C sous le freinage des Intel récents).</summary>
    public const double SeuilChaleurC = ReleveDesParties.SeuilChaleurC;

    /// <summary>**Non chiffré**, choix DrDXT : au-delà d'une semaine sans vrai redémarrage (le démarrage rapide ne compte
    /// pas, il reprend le noyau), les fuites de mémoire des pilotes et des programmes ouverts s'accumulent, et une mise à
    /// jour peut attendre son redémarrage.</summary>
    public const int SeuilJoursSansRedemarrage = 7;

    /// <summary>**Non chiffré**, choix DrDXT : un demi-gigaoctet de mémoire vidéo tenu par un autre programme, c'est le
    /// budget de textures d'un réglage entier sur une carte de 8 Go. Le compositeur du bureau (dwm) et DrDXT ne comptent pas.</summary>
    public const long SeuilMemoireVideoOctets = 512L * 1024 * 1024;

    private static readonly string[] SansCompter = ["dwm", "DrDxt.App", "Idle", "System"];

    /// <summary>Les gênes, en phrases ; vide = rien ne gêne parmi ce qui a pu être lu.</summary>
    public static IReadOnlyList<string> Genes(EtatAvantPartie e, Func<string, string> localiser, DateTimeOffset? maintenant = null)
    {
        var fr = System.Globalization.CultureInfo.CurrentCulture;
        string F(string cle, params object[] a) => string.Format(fr, localiser(cle), a);
        var genes = new List<string>();
        if (e.Voisins?.Where(v => v.ProcesseurPourcent >= SeuilVoisinPourcent).OrderByDescending(v => v.ProcesseurPourcent).Take(3).ToList()
            is { Count: > 0 } lourds)
            genes.Add(F("PretAJouer.Voisins", string.Join(", ", lourds.Select(v => $"{v.Nom} ({v.ProcesseurPourcent:0} %)"))));
        // Une perte vers la box est un vrai problème de lien local ; vers internet, un ping isolé
        // perdu (limitation des réponses) ou filtré à 100 % (pare-feu, opérateur) n'en est pas un. Dite à partir de 2 sur 10,
        // et pas quand aucun ne revient alors que la box répond (filtrage, rien de mesuré).
        if (e.PertesBoxPourcent > 0 || e.PertesInternetPourcent is >= 20 and < 100)
            // Un côté non mesuré (sans passerelle lue : VPN, partage de connexion) se dit « ? », jamais « 0 % ».
            genes.Add(F("PretAJouer.Pertes", e.PertesBoxPourcent?.ToString("0", fr) ?? "?", e.PertesInternetPourcent?.ToString("0", fr) ?? "?"));
        if (e.PingBoxMs >= SeuilBoxMs)
            genes.Add(F("PretAJouer.Box", e.PingBoxMs.Value.ToString("0", fr)));
        if (e.MemoireLibreOctets < SeuilMemoireOctets)
            genes.Add(F("PretAJouer.Memoire", (e.MemoireLibreOctets.Value / 1024 / 1024).ToString("0", fr)));
        if (e.Celsius >= SeuilChaleurC)
            genes.Add(F("PretAJouer.Chaleur", e.Celsius.Value.ToString("0", fr)));
        if (e.DernierDemarrage is { } d && (maintenant ?? DateTimeOffset.Now) - d is { TotalDays: >= SeuilJoursSansRedemarrage } depuis)
            genes.Add(F("PretAJouer.Redemarrage", ((int)depuis.TotalDays).ToString(fr)));
        if (e.MemoireVideo?.Where(p => p.Octets >= SeuilMemoireVideoOctets && !SansCompter.Contains(p.Nom, StringComparer.OrdinalIgnoreCase))
                .Take(3).ToList() is { Count: > 0 } video)
            genes.Add(F("PretAJouer.MemoireVideo", string.Join(", ", video.Select(p => $"{p.Nom} ({p.Octets / 1024 / 1024} {localiser("Unite.Mo")})"))));
        if (e.AudioVirtuel is { Count: > 0 } audio)
            genes.Add(F("PretAJouer.AudioVirtuel", string.Join(", ", audio)));
        if (e.ConversionDuDisque == true)
            genes.Add(localiser("PretAJouer.Conversion"));
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
