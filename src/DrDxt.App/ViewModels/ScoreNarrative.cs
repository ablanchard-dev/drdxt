using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>Le compte des constats confirmés (gravité majeure). Il compte des constats, jamais des points.</summary>
public static class ScoreNarrative
{
    /// <summary>Nombre de constats qui sont un problème confirmé (sévérité Major).</summary>
    public static int CriticalCount(IEnumerable<FindingViewModel> findings) =>
        findings.Count(f => f.Severity == Severity.Major);
}
