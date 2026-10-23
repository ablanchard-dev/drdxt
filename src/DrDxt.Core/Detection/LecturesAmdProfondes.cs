namespace DrDxt.Core;

/// <summary>
/// Les lectures AMD profondes d'un Ryzen (timings appliqués, réglé contre appliqué, table PM, BIOS AMD nommé, température,
/// cause matérielle des redémarrages) passent par les modules de lecture AMD de PawnIO, embarqués et en lecture seule. Elles
/// ne s'ouvrent que sur une génération où un rapport de validation sur une vraie machine les a vérifiées
/// (<see cref="ValideesEnVie"/>), et seulement quand PawnIO est installé. Porte fermée : une seule ligne le dit, avec la
/// liste des lectures, plutôt qu'une ligne « non lu » par lecture. Pur, lecture seule.
/// </summary>
public static class LecturesAmdProfondes
{
    /// <summary>Les générations (<see cref="GenerationAmd.Ligne.Generation"/>) dont un rapport de validation, dans
    /// tests/Shared/validations, prouve ces lectures contre un outil de référence. Aucune encore : DrDXT n'a pas de
    /// machine AMD.</summary>
    public static readonly IReadOnlySet<string> ValideesEnVie = new HashSet<string>();

    /// <param name="pawnIoInstalle">PawnIO est-il installé sur la machine (DrDXT ne l'installe jamais) ?</param>
    /// <param name="constats">Les constats détaillés ; calculés seulement quand la porte est ouverte.</param>
    /// <param name="validees">Les générations validées ; <see cref="ValideesEnVie"/> par défaut.</param>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, bool pawnIoInstalle,
        Func<IEnumerable<RepairFinding>> constats, IReadOnlySet<string>? validees = null)
    {
        if (generation is null) return [];
        if (!pawnIoInstalle)
            return [Fermee(Docteur.T("LAP.SansPawnIo", Docteur.T("LAP.Liste")))];
        if (!(validees ?? ValideesEnVie).Contains(generation.Generation))
            return [Fermee(Docteur.T("LAP.NonValidee", Docteur.T("LAP.Liste"), generation.Generation))];
        return constats();
    }

    private static RepairFinding Fermee(string explication)
        => new("CPU", "lectures AMD profondes", null, null, RepairStatus.Unknown, explication);
}
