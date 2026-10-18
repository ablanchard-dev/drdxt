namespace DrDxt.Core;

/// <summary>
/// Les lectures AMD profondes d'un Ryzen (timings appliqués, réglé contre appliqué, table PM, BIOS AMD nommé) passent par un
/// module de lecture AMD. Tant qu'il n'est pas installé, une seule ligne le dit, avec la liste des quatre lectures, plutôt
/// que quatre « non lu » ; dès qu'il l'est, chaque constat reprend sa ligne. Pur, lecture seule.
/// </summary>
public static class LecturesAmdProfondes
{
    /// <summary>Le module de lecture AMD (PawnIO, SMN et SMU en lecture) : DrDXT ne l'embarque pas encore (GATES).</summary>
    public static bool ModuleInstalle { get; } = false;

    /// <param name="constats">Les quatre constats détaillés ; calculés seulement quand la porte est ouverte.</param>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, bool porteOuverte,
        Func<IEnumerable<RepairFinding>> constats)
    {
        if (generation is null) return [];
        if (porteOuverte) return constats();
        return [new RepairFinding("CPU", "lectures AMD profondes", null, null, RepairStatus.Unknown,
            Docteur.T("LAP.Fermee", Docteur.T("LAP.Liste")))];
    }
}
