using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// L'installeur ne livre qu'un PresentMonService.exe : celui de la racine de {app}. Les copies de repli que les publish posent
/// sous third_party\presentmon (celle de l'App, celle du service) ne sont jamais lues. Ce test garde l'exclusion ET les faits
/// qui la rendent sûre : si un jour le service émettait l'installation de PresentMon, ou si un autre émetteur apparaissait, sa
/// copie redeviendrait la seule qu'il trouve, et l'exclusion casserait la mesure. Le test tombe avant.
/// </summary>
public sealed class InstallateurPresentMonTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    // Élagué avant de descendre : la suite ne lit jamais la sortie de build que MSBuild écrit pendant qu'elle tourne.
    private static IEnumerable<string> Sources(string dossier) => FichiersDuDepot.Sous(Path.Combine(Racine(), dossier), "*.cs");

    [Fact]
    public void L_installeur_exclut_les_copies_de_repli_de_PresentMon()
    {
        var iss = File.ReadAllText(Path.Combine(Racine(), "installer", "DrDXT.iss"));
        var ligne = iss.Split('\n').Single(l => l.StartsWith("Source: \"publish\\*\"", StringComparison.Ordinal));

        ligne.Should().Contain("*.pdb").And.Contain(@"\third_party").And.Contain(@"\service\third_party");
        // Une mise à jour n'efface que ce qu'elle remplace : les copies d'une ancienne version partent explicitement.
        iss.Should().Contain(@"Type: filesandordirs; Name: ""{app}\third_party""")
            .And.Contain(@"Type: filesandordirs; Name: ""{app}\service\third_party""");
        iss.Should().Contain(@"ExpandConstant('{app}\PresentMonService.exe')",
            "le service PresentMon s'enregistre depuis la racine de {app}, la seule copie livrée");
    }

    [Fact]
    public void Seule_l_App_emet_l_installation_de_PresentMon_jamais_le_service()
    {
        // Le contrat et son décodage (DrDxt.Core) construisent l'op sans l'émettre ; tout le reste de src/ l'émettrait.
        var emetteurs = Sources("src")
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}DrDxt.Core{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("new InstallPresentMonServiceOp(", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        emetteurs.Should().Equal(["SelfHealingBenchmarkRunner.cs"], "un seul émetteur : le banc de l'App, qui passe par le helper de {app}");

        var service = Sources(Path.Combine("src", "DrDxt.ReconcilerHost")).Select(File.ReadAllText).ToList();
        service.Should().NotBeEmpty("témoin : les sources du service sont lues");
        service.Should().NotContain(t => t.Contains("SelfHealingBenchmarkRunner", StringComparison.Ordinal)
                                         || t.Contains("AddDrDxtMeasurement", StringComparison.Ordinal)
                                         || t.Contains("InstallPresentMonServiceOp", StringComparison.Ordinal),
            "le service n'a ni banc ni mesure : sa copie de PresentMon ne serait jamais lue");
    }
}
