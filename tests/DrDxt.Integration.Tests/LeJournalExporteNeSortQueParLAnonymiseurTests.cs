using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// IDiagnosticsLog.ExportAsync copie le journal tel quel : valeurs de registre, chemins du compte. Un code du produit qui
// l'appelle fait sortir ce texte de la machine ; il passe d'abord par l'anonymiseur unique, MachineExportee.AnonymiserTexte.
public sealed class LeJournalExporteNeSortQueParLAnonymiseurTests
{
    private static readonly Regex AppelDExport = new(@"\.ExportAsync\(");

    [Fact]
    public void Le_crible_reconnait_un_appel_d_export()
        => AppelDExport.IsMatch("var chemin = await journal.ExportAsync();").Should().BeTrue();

    [Fact]
    public void Chaque_appelant_de_l_export_du_journal_passe_par_l_anonymiseur()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;

        var sources = FichiersDuDepot.Sous(Path.Combine(d!.FullName, "src"), "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Nom: Path.GetFileName(f), Texte: File.ReadAllText(f))).ToList();
        // Témoin : le crible lit bien les deux journaux qui définissent l'export.
        sources.Count(s => s.Texte.Contains("Task<Result<string>> ExportAsync()", StringComparison.Ordinal)).Should().BeGreaterThan(1);

        sources.Where(s => AppelDExport.IsMatch(s.Texte) && !s.Texte.Contains("MachineExportee.AnonymiserTexte(", StringComparison.Ordinal))
            .Select(s => s.Nom).Should().BeEmpty();
    }
}
