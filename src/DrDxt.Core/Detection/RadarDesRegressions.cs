using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Le radar des régressions connues : prévient quand une mise à jour de Windows installée, la version d'un pilote de
/// carte ou le processeur figure dans une régression publiée par Microsoft ou un fabricant. Les règles viennent des
/// tables en service (<see cref="DonneesEnService"/>), livrées avec l'App ou reçues signées sans changer de binaire ;
/// chaque verdict cite la source et la date où elle a été relevée. Lecture seule : rien n'est désinstallé ni écrit.
/// </summary>
public static class RadarDesRegressions
{
    public const string Domaine = "WINDOWS UPDATE";
    public const string Nom = "radar des regressions connues";

    private enum Verdict { Concerne, PasConcerne, NonJuge }

    /// <summary>Pur : le constat dit-il un radar sans aucune règle ? Sa valeur est le texte de RR.ValeurVide, reconnu dans
    /// chaque langue du docteur : un constat dit dans une langue se juge aussi dans l'autre.</summary>
    public static bool TableVide(RepairFinding c) => c.Nom == Nom && Docteur.EstLeTexteDe("RR.ValeurVide", c.ValeurCourante);

    /// <summary>Pur. <paramref name="kbInstalles"/> : les numéros KB installés (null = historique non lu) ;
    /// <paramref name="cartes"/> : chaque carte d'affichage et la version de son pilote (null = non lues) ;
    /// <paramref name="processeur"/> : le nom du processeur (null = non lu).</summary>
    public static IEnumerable<RepairFinding> Juger(TablesDeDonnees tables, IReadOnlyList<string>? kbInstalles,
        IReadOnlyList<(string Nom, string? Version)>? cartes, string? processeur)
    {
        if (kbInstalles is null)
        {
            yield return new RepairFinding(Domaine, Nom, null, null, RepairStatus.Unknown, Docteur.T("RR.NonLu"));
            yield break;
        }
        if (tables.Radar.Count == 0)
        {
            yield return new RepairFinding(Domaine, Nom, Docteur.T("RR.ValeurVide"), null, RepairStatus.Observed, Docteur.T("RR.Vide", tables.Version));
            yield break;
        }

        var installes = new HashSet<string>(kbInstalles, StringComparer.OrdinalIgnoreCase);
        var concernees = 0;
        var nonJugees = 0;
        foreach (var r in tables.Radar)
        {
            switch (Evaluer(r, installes, cartes, processeur))
            {
                case Verdict.Concerne:
                    concernees++;
                    var texte = Docteur.Langue.TwoLetterISOLanguageName == "fr" ? r.Fr : r.En;
                    yield return new RepairFinding(Domaine, Docteur.Nom("RR.NomRegle", r.Id), r.Kb ?? r.Carte ?? r.Processeur, null,
                        RepairStatus.Suspicious,
                        Docteur.T("RR.Concerne", texte, r.Source, r.Releve.ToString(Docteur.T("Format.Date"), CultureInfo.InvariantCulture), tables.Version),
                        Remede: r.CorrigeePar is { } c ? Docteur.T("RR.Remede", c) : null);
                    break;
                case Verdict.NonJuge:
                    nonJugees++;
                    break;
            }
        }
        if (concernees > 0) yield break;
        yield return nonJugees > 0
            ? new RepairFinding(Domaine, Nom, null, null, RepairStatus.Unknown, Docteur.T("RR.NonJugees", tables.Radar.Count, nonJugees, tables.Version))
            : new RepairFinding(Domaine, Nom, null, null, RepairStatus.Observed, Docteur.T("RR.AucuneConcerne", tables.Radar.Count, tables.Version));
    }

    private static Verdict Evaluer(RegleDeRegression r, HashSet<string> installes,
        IReadOnlyList<(string Nom, string? Version)>? cartes, string? processeur)
    {
        if (r.CorrigeePar is { } corrige && installes.Contains(corrige)) return Verdict.PasConcerne;
        if (r.Kb is { } kb && !installes.Contains(kb)) return Verdict.PasConcerne;

        if (r.Processeur is { } p)
        {
            if (processeur is null) return Verdict.NonJuge;
            if (!processeur.Contains(p, StringComparison.OrdinalIgnoreCase)) return Verdict.PasConcerne;
        }

        if (r.Carte is { } carte)
        {
            if (cartes is null) return Verdict.NonJuge;
            var visees = cartes.Where(c => c.Nom.Contains(carte, StringComparison.OrdinalIgnoreCase)).ToList();
            if (visees.Count == 0) return Verdict.PasConcerne;
            if (r.PiloteDepuis is null && r.PiloteJusqua is null) return Verdict.Concerne;
            var dansLaPlage = false;
            var illisible = false;
            foreach (var (_, version) in visees)
            {
                if (version is null || Comparer(version, version) is null) { illisible = true; continue; }
                if ((r.PiloteDepuis is null || Comparer(version, r.PiloteDepuis) >= 0)
                    && (r.PiloteJusqua is null || Comparer(version, r.PiloteJusqua) <= 0))
                    dansLaPlage = true;
            }
            if (dansLaPlage) return Verdict.Concerne;
            return illisible ? Verdict.NonJuge : Verdict.PasConcerne;
        }
        return Verdict.Concerne;
    }

    /// <summary>Pur : deux versions de pilote Windows (« 32.0.15.6094 ») comparées nombre à nombre ; null si l'une ne se
    /// lit pas. « 32.0.15.6100 » vient après « 32.0.15.6094 », ce qu'une comparaison de textes rate.</summary>
    public static int? Comparer(string a, string b)
    {
        var x = a.Trim().Split('.');
        var y = b.Trim().Split('.');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long u = 0, v = 0;
            if (i < x.Length && !long.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out u)) return null;
            if (i < y.Length && !long.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out v)) return null;
            if (u != v) return u.CompareTo(v);
        }
        return 0;
    }
}
