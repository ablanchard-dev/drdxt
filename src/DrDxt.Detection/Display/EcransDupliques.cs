using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// Un seul écran, des écrans étendus ou dupliqués. Une duplication entre deux fréquences différentes est dite, avec les
/// deux fréquences ; ce qu'elle coûte à l'écran rapide n'a ni source publique ni relevé (GATES), et n'est pas chiffré.
/// Lecture seule.
/// </summary>
/// <remarks>
/// Les sources et les sorties viennent de <see cref="CheminsDAffichage"/> (QueryDisplayConfig, Microsoft Learn). Deux
/// fréquences à moins de <see cref="ToleranceHz"/> l'une de l'autre sont la même (59,94 et 60 Hz). Le plafond propre de
/// chaque écran reste jugé par <see cref="EcranBride"/>, sur son EDID complet.
/// </remarks>
public static class EcransDupliques
{
    public const double ToleranceHz = 0.5;

    public static RepairFinding Juger(IReadOnlyList<CheminsDAffichage.Chemin>? chemins)
    {
        const string nom = "topologie d'affichage";
        // Aucun chemin actif (session à distance, écran éteint) n'est pas « un seul écran ».
        if (chemins is null or { Count: 0 })
            return new RepairFinding("DISPLAY", nom, null, null, RepairStatus.Unknown, Docteur.T("TOPO.NonLu"));
        if (chemins.Count <= 1)
            return new RepairFinding("DISPLAY", nom, Docteur.T("TOPO.ValeurUnique"), null, RepairStatus.ExpectedDefault, Docteur.T("TOPO.Unique"));
        var groupes = chemins.GroupBy(c => (c.SourceCarte, c.SourceId)).ToList();
        var dupliques = groupes.Where(g => g.Count() > 1).ToList();
        if (dupliques.Count == 0)
            return new RepairFinding("DISPLAY", nom, Docteur.T("TOPO.ValeurEtendue", chemins.Count), null, RepairStatus.ExpectedDefault,
                Docteur.T("TOPO.Etendue", chemins.Count));
        var comparees = false;
        foreach (var g in dupliques)
        {
            var lus = g.Where(c => c.Hz is not null).ToList();
            if (lus.Count < 2) continue;
            comparees = true;
            var lent = lus.MinBy(c => c.Hz)!;
            var rapide = lus.MaxBy(c => c.Hz)!;
            if (rapide.Hz!.Value - lent.Hz!.Value <= ToleranceHz) continue;
            // Observé, pas suspect : le coût de la duplication n'a ni source ni relevé, seules les deux fréquences sont dites.
            return new RepairFinding("DISPLAY", nom, Docteur.T("TOPO.ValeurDupliqueeHz", Hz(rapide.Hz.Value), Hz(lent.Hz.Value)),
                Docteur.T("TOPO.ValeurEtendueSeule"), RepairStatus.Observed,
                Docteur.T("TOPO.DupliqueeCoute", Nom(rapide), Hz(rapide.Hz.Value), Nom(lent), Hz(lent.Hz.Value)),
                RepairAction.Recommandation);
        }
        // « Même fréquence » seulement si deux fréquences au moins ont été lues et comparées.
        return comparees
            ? new RepairFinding("DISPLAY", nom, Docteur.T("TOPO.ValeurDupliquee"), null, RepairStatus.Observed, Docteur.T("TOPO.DupliqueeMeme"))
            : new RepairFinding("DISPLAY", nom, Docteur.T("TOPO.ValeurDupliquee"), null, RepairStatus.Unknown, Docteur.T("TOPO.DupliqueeNonLue"));
    }

    private static string Nom(CheminsDAffichage.Chemin c) => c.Nom ?? Docteur.T("TOPO.EcranSansNom");
    private static string Hz(double hz) => hz.ToString("0.##", Docteur.Langue);
}
