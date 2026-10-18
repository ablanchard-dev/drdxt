namespace DrDxt.Core;

/// <summary>Une seconde de la table PM : chaque valeur et sa limite. PPT en W, TDC et EDC en A, température en °C.</summary>
public sealed record LimitesAmd(float Ppt, float PptLimite, float Tdc, float TdcLimite, float Edc, float EdcLimite,
    float Temperature, float TemperatureLimite);

/// <summary>Les secondes d'une partie mises ensemble : combien ont buté sur chaque limite.</summary>
public sealed record BridageAmd(int Secondes, int Ppt, int Tdc, int Edc, int Thermique)
{
    /// <summary>Une valeur à 97 % de sa limite ou plus y bute. Choix de DrDXT, non chiffré : sous la limite, la régulation
    /// du SMU laisse quelques pour cent de marge.</summary>
    public const double PartDeLaLimite = 0.97;

    /// <summary>Sous 5 % du temps, une limite ne coûte presque rien : même seuil que <see cref="FreinageCpu.PartNegligeable"/>.</summary>
    public const double PartNegligeable = FreinageCpu.PartNegligeable;

    /// <summary>Pur. La température compte à sa limite atteinte, en degrés : jamais comme une part de la limite, qui n'a pas
    /// de sens pour des degrés Celsius.</summary>
    public static BridageAmd? Depuis(IEnumerable<LimitesAmd> secondes)
    {
        var s = secondes.ToList();
        if (s.Count == 0) return null;
        return new BridageAmd(s.Count, s.Count(x => x.Ppt >= PartDeLaLimite * x.PptLimite), s.Count(x => x.Tdc >= PartDeLaLimite * x.TdcLimite),
            s.Count(x => x.Edc >= PartDeLaLimite * x.EdcLimite), s.Count(x => x.Temperature >= x.TemperatureLimite));
    }

    public double Part(int secondes) => Secondes == 0 ? 0 : (double)secondes / Secondes;
}

/// <summary>
/// Les limites PPT, TDC, EDC et thermique d'un Ryzen, lues dans la table PM que le SMU copie en mémoire. Pur : décode une
/// table déjà copiée. Une version n'est décodée que sur sa génération et à la taille exacte que sa version annonce.
/// Sources, lues comme documentation (AMD ne publie pas ces tables) : tailles, ryzen_smu (github.com/amkillam/ryzen_smu,
/// commit d2983668, smu.c, smu_update_pmtable_size) ; positions, ryzen_monitor (github.com/hattedsquirrel/ryzen_monitor,
/// commit 7529c7cf, src/pm_tables.c) : PPT limite et valeur aux flottants 0 et 1, TDC 2 et 3, thermique 4 et 5, EDC 8 et 9.
/// Les versions que ryzen_monitor dit devinées (0x380905) ne sont pas reprises ; aucune source publique ne donne ces
/// positions pour Zen 4 et Zen 5, qui restent non lus. Aucune lecture : le module PawnIO AMD manque (GATES).
/// </summary>
public static class TablePmAmd
{
    /// <param name="Taille">Octets que le SMU copie pour cette version (ryzen_smu).</param>
    public sealed record Version(uint Numero, int Famille, int Modele, int Taille);

    // En commentaire, ce que ryzen_monitor dit de chaque disposition.
    public static readonly IReadOnlyList<Version> Connues =
    [
        new(0x240803, 0x17, 0x71, 0x7E4), // reconstruite depuis des copies de table publiées (issue 2), non testée par l'auteur
        new(0x240903, 0x17, 0x71, 0x518), // reprise du moniteur de ryzen_smu, non testée par l'auteur
        new(0x380804, 0x19, 0x21, 0x8A4), // testée par l'auteur sur un Ryzen 5900X
        new(0x380805, 0x19, 0x21, 0x8F0), // testée par l'auteur sur un Ryzen 5900X
        new(0x380904, 0x19, 0x21, 0x5A4), // testée sommairement sur un Ryzen 5600X (issue 1)
    ];

    /// <summary>Pur : la seconde lue, ou null (version inconnue de cette puce, taille différente, valeur non plausible).</summary>
    public static LimitesAmd? Decoder(int famille, int modele, uint version, ReadOnlySpan<byte> table)
    {
        var v = Connues.FirstOrDefault(c => c.Numero == version && c.Famille == famille && c.Modele == modele);
        if (v is null || table.Length != v.Taille) return null;
        var octets = table.ToArray();
        float F(int i) => BitConverter.ToSingle(octets, i * 4);
        var l = new LimitesAmd(F(1), F(0), F(3), F(2), F(9), F(8), F(5), F(4));
        float[] tout = [l.Ppt, l.PptLimite, l.Tdc, l.TdcLimite, l.Edc, l.EdcLimite, l.Temperature, l.TemperatureLimite];
        if (tout.Any(x => !float.IsFinite(x) || x < 0)) return null;
        if (l.PptLimite <= 0 || l.TdcLimite <= 0 || l.EdcLimite <= 0 || l.TemperatureLimite is <= 0 or > 150 || l.Temperature > 150) return null;
        return l;
    }

    /// <summary>Pur : le constat sur une machine AMD reconnue. <paramref name="bridage"/> null = table non lue pendant les
    /// parties.</summary>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, BridageAmd? bridage)
    {
        if (generation is null) yield break;
        if (bridage is null)
        {
            yield return new RepairFinding("CPU", "limites PPT/TDC/EDC (AMD)", null, null, RepairStatus.Unknown, Docteur.T("PMA.NonLue"));
            yield break;
        }
        var parts = new (string Nom, int Secondes)[] { ("PPT", bridage.Ppt), ("TDC", bridage.Tdc), ("EDC", bridage.Edc), ("Tjmax", bridage.Thermique) };
        var bridants = parts.Where(p => bridage.Part(p.Secondes) >= BridageAmd.PartNegligeable).ToList();
        var valeur = string.Join(", ", parts.Select(p => string.Format(Docteur.Langue, "{0} {1:P0}", p.Nom, bridage.Part(p.Secondes))));
        yield return bridants.Count == 0
            ? new RepairFinding("CPU", "limites PPT/TDC/EDC (AMD)", valeur, null, RepairStatus.Observed, Docteur.T("PMA.RienNeBride", bridage.Secondes))
            : new RepairFinding("CPU", "limites PPT/TDC/EDC (AMD)", valeur, null, RepairStatus.Observed,
                Docteur.T("PMA.Bride", string.Join(", ", bridants.Select(p => string.Format(Docteur.Langue, "{0} {1:P0}", p.Nom, bridage.Part(p.Secondes)))), bridage.Secondes));
    }
}
