namespace DrDxt.Core;

/// <summary>Une ligne du bilan santé : un titre fixe et le constat qui la remplit.</summary>
public sealed record LigneBilan(string Titre, Constat Constat);

/// <summary>
/// Le bilan santé en huit lignes fixes, prises dans les constats du docteur. Une ligne
/// sans constat est dite non lue, jamais « bonne » ; la ligne du point de restauration est sans objet tant qu'aucun souci
/// n'est en cours (le docteur ne la produit qu'alors).
/// </summary>
public static class BilanSante
{
    private static readonly (string Titre, string Domaine, Func<string, bool> Nom)[] Lignes =
    [
        ("BS.TempProcesseur", "THERMIQUE", n => n == "temperature du processeur"),
        ("BS.TempCarte", "THERMIQUE", n => n == "temperature du GPU"),
        ("BS.Disques", "STOCKAGE", n => n.StartsWith("usure", StringComparison.Ordinal) || n.StartsWith("erreurs non corrigées", StringComparison.Ordinal)),
        ("BS.Arrets", "STABILITE", n => n == "arrets brutaux"),
        ("BS.Whea", "STABILITE", n => n.StartsWith("erreurs matérielles", StringComparison.Ordinal)),
        ("BS.MisesAJour", "WINDOWS UPDATE", n => n == "en attente" || n == "echecs"),
        ("BS.Pilote", "GPU DRIVER", n => n.StartsWith("pilote", StringComparison.Ordinal) || n == "version du pilote graphique"),
        ("BS.PointRestauration", "STABILITE", n => n == "point de restauration sain"),
    ];

    public static IReadOnlyList<LigneBilan> Construire(IEnumerable<RepairFinding> constats)
    {
        var tous = constats.ToList();
        return Lignes.Select((l, i) =>
        {
            // Le plus grave d'abord : un disque qui perd des données passe devant un disque peu usé.
            var trouve = tous.Where(c => c.Domaine == l.Domaine && l.Nom(c.Nom))
                // À gravité égale, le non lu passe devant le lu (il était derrière).
                .OrderByDescending(c => Constat.GraviteDe(c.Statut)).ThenByDescending(c => c.Statut == RepairStatus.Unknown)
                // À gravité égale, ce qui attend dans Windows Update passe devant le cumul des échecs.
                .ThenBy(c => c.Nom != "en attente")
                .FirstOrDefault();
            var titre = Docteur.T(l.Titre);
            return new LigneBilan(titre, trouve is not null ? Constat.Depuis(trouve)
                : new Constat($"bilan/{i}", "sante", l.Domaine, Severity.Info,
                    i == Lignes.Length - 1 ? EtatConstat.SansObjet : EtatConstat.NonLu, titre, null, null,
                    i == Lignes.Length - 1 ? Docteur.T("BS.AucunSouci") : Docteur.T("BS.PasLu"),
                    null, null, Annulation.SansObjet));
        }).ToList();
    }
}
