using System.Globalization;

namespace DrDxt.Core;

/// <summary>Un ventilateur de la carte mère sur une partie : régime moyen et minimal, secondes à l'arrêt, secondes lues.</summary>
public sealed record VentilateurEnPartie(int Index, double TrMinMoyen, int TrMinMin, int SecondesArret, int Secondes, string? Nom = null)
{
    /// <summary>Lu par HWiNFO (secours) plutôt que par la puce de la carte.</summary>
    public bool ParHwinfo => Index >= VentilateurLu.DebutHwinfo;
}

/// <summary>
/// Un ventilateur qui frotte et s'arrête (après un nettoyage, par exemple) passe inaperçu jusqu'au bruit : chaque partie garde chaque ventilateur de la carte mère (lus par la puce Super I/O, toutes les quinze
/// secondes), et le docteur dit celui qui s'arrête pendant la partie ou qui ne tourne plus d'une partie à l'autre. Un en-tête qui
/// n'a jamais tourné (rien de branché) n'est pas gardé. Limite dite : les cartes dont la puce est lue (MSI, NCT6687).
/// </summary>
public static class VentilateursEnPartie
{
    /// <summary>Sous ce régime, un ventilateur est à l'arrêt ; au-dessus de <see cref="TrMinTourne"/>, il tourne.</summary>
    public const int TrMinArret = 200;
    public const int TrMinTourne = 300;
    /// <summary>Deux lectures de la puce à l'arrêt au moins pour le dire : elle se lit toutes les 15 s (PawnIoSuperIo.IntervalleLecture)
    /// et chaque lecture compte pour 15 s, donc une seule lecture ratée fait déjà 15 s. **Non chiffré**. Limite : un arrêt de
    /// moins de 30 s peut ne pas se voir.</summary>
    public const int SecondesArretMinimum = 30;
    /// <summary>Deux parties d'avant où il tournait, pour dire qu'il ne tourne plus.</summary>
    public const int PartiesAvantMinimum = 2;

    public static IReadOnlyList<VentilateurEnPartie>? Depuis(IEnumerable<IReadOnlyList<VentilateurLu>?> secondes)
    {
        var lues = secondes.OfType<IReadOnlyList<VentilateurLu>>().ToList();
        if (lues.Count == 0) return null;
        return lues.SelectMany(s => s).GroupBy(v => v.Index)
            .Where(g => g.Any(v => v.TrMin > TrMinTourne))
            .Select(g => new VentilateurEnPartie(g.Key, g.Average(v => v.TrMin), g.Min(v => v.TrMin), g.Count(v => v.TrMin < TrMinArret), g.Count(),
                g.Select(v => v.Nom).FirstOrDefault(n => n is not null)))
            .OrderBy(v => v.Index).ToList();
    }

    public static IReadOnlyList<VentilateurEnPartie>? Cumuler(IEnumerable<IReadOnlyList<VentilateurEnPartie>?> morceaux)
    {
        var m = morceaux.OfType<IReadOnlyList<VentilateurEnPartie>>().ToList();
        if (m.Count == 0) return null;
        return m.SelectMany(x => x).GroupBy(v => v.Index)
            .Select(g => new VentilateurEnPartie(g.Key, g.Sum(v => v.TrMinMoyen * v.Secondes) / Math.Max(1, g.Sum(v => v.Secondes)),
                g.Min(v => v.TrMinMin), g.Sum(v => v.SecondesArret), g.Sum(v => v.Secondes), g.Select(v => v.Nom).FirstOrDefault(n => n is not null)))
            .OrderBy(v => v.Index).ToList();
    }

    /// <summary>Ce que le docteur lit d'une partie : le jeu, sa fin, ses ventilateurs (vide = non lus).</summary>
    public sealed record PartieVue(string Jeu, DateTimeOffset Fin, IReadOnlyList<VentilateurEnPartie> Ventilateurs);

    /// <summary>Pur : les parties, la plus récente d'abord ou non ; rien quand la dernière n'a pas lu ses ventilateurs.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<PartieVue> parties)
    {
        var triees = parties.OrderByDescending(p => p.Fin).ToList();
        if (triees.Count == 0) yield break;
        // Des parties relevées, aucun ventilateur lu sur aucune : « non lu », avec le geste qui les rend
        // lisibles, plutôt qu'un silence qui passerait pour « tout va bien ».
        if (triees.All(p => p.Ventilateurs.Count == 0))
        {
            yield return new RepairFinding("THERMIQUE", "ventilateurs non lus", null, null, RepairStatus.Unknown,
                Docteur.T("VE.NonLus", triees.Count));
            yield break;
        }
        if (triees[0].Ventilateurs.Count == 0) yield break;
        var derniere = triees[0];
        var inv = CultureInfo.InvariantCulture;
        foreach (var v in derniere.Ventilateurs.Where(v => v.SecondesArret >= SecondesArretMinimum))
            yield return new RepairFinding("THERMIQUE", "ventilateur qui s'arrête", Docteur.T("VE.ArretValeur", Nommer(v), v.SecondesArret),
                Docteur.T("VE.JamaisArret"), RepairStatus.Suspicious,
                Docteur.T("VE.Arret", derniere.Jeu, derniere.Fin.ToLocalTime(), Nommer(v), v.SecondesArret.ToString(inv),
                    v.TrMinMoyen.ToString("0", inv)),
                RepairAction.Recommandation);
        // Seules les parties lues par la même source se comparent (puce de la carte ou HWiNFO) : un changement de source ne
        // fait jamais dire « ne tourne plus ».
        var source = derniere.Ventilateurs[0].ParHwinfo;
        var avant = triees.Skip(1).Where(p => p.Ventilateurs.Count > 0 && p.Ventilateurs[0].ParHwinfo == source).ToList();
        var presents = derniere.Ventilateurs.Select(v => v.Index).ToHashSet();
        foreach (var index in avant.SelectMany(p => p.Ventilateurs).Select(v => v.Index).Distinct().Where(i => !presents.Contains(i)))
        {
            var tournait = avant.Count(p => p.Ventilateurs.Any(v => v.Index == index && v.TrMinMoyen > TrMinTourne));
            var connu = avant.SelectMany(p => p.Ventilateurs).First(v => v.Index == index);
            if (tournait < PartiesAvantMinimum) continue;
            yield return new RepairFinding("THERMIQUE", "ventilateur qui ne tourne plus", Docteur.T("VE.ZeroValeur", Nommer(connu)),
                Docteur.T("VE.Tournait"), RepairStatus.Suspicious,
                Docteur.T("VE.Mort", Nommer(connu), tournait, derniere.Jeu, derniere.Fin.ToLocalTime()), RepairAction.Recommandation);
        }
    }

    /// <summary>Le nom à dire : celui de HWiNFO quand il vient de lui, sinon l'en-tête de la carte.</summary>
    public static string Nommer(VentilateurEnPartie v) => v.Nom is { } n ? Docteur.T("VE.Nomme", n) : Nom(v.Index);

    /// <summary>L'en-tête, tel que la carte le nomme quand c'est connu (cartes MSI : 0 = CPU_FAN, 1 = pompe, 2 à 7 = SYS_FAN 1 à 6).</summary>
    public static string Nom(int index) => index switch
    {
        0 => Docteur.T("VE.Cpu"),
        1 => Docteur.T("VE.Pompe"),
        _ => Docteur.T("VE.Sys", index, index - 1),
    };
}
