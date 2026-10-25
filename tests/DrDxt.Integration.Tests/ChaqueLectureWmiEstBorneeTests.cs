using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Toute lecture WMI passe par LectureWmiBornee : System.Management n'a de délai ni pour la connexion ni pour l'exécution, et un
/// winmgmt figé figerait le scan, le commit du manifeste ou le tour du service. Dans chaque fichier de src, autant de passages
/// par la borne que de requêtes construites, aux exceptions près, chacune avec sa raison.
/// </summary>
public sealed class ChaqueLectureWmiEstBorneeTests
{
    private static readonly Regex Requete = new(@"new\s+(System\.Management\.)?(ManagementObjectSearcher|ManagementClass)\(");
    private static readonly Regex Borne = new(@"LectureWmiBornee\.Lire\b");

    /// <summary>Les requêtes qui ne sont pas des lectures, par fichier.</summary>
    private static readonly Dictionary<string, (int Nombre, string Raison)> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Win32RestorePointActions.cs"] = (1, "EnableComputerRestore est une écriture, pas une lecture : elle ne s'abandonne pas en cours de route"),
    };

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Chaque_requete_wmi_passe_par_la_borne()
    {
        var fichiers = FichiersDuDepot.Sous(Path.Combine(Racine(), "src"), "*.cs")
            .Select(f => (Nom: Path.GetFileName(f), Texte: File.ReadAllText(f)))
            .Where(f => Requete.IsMatch(f.Texte))
            .ToList();

        fichiers.Should().HaveCountGreaterThan(10, "témoin : les lecteurs WMI connus sont trouvés");
        fichiers.Select(f => (f.Nom, Requetes: Requete.Matches(f.Texte).Count, Bornes: Borne.Matches(f.Texte).Count))
            .Where(f => f.Bornes + (Exceptions.TryGetValue(f.Nom, out var e) ? e.Nombre : 0) < f.Requetes)
            .Select(f => $"{f.Nom} : {f.Requetes} requête(s), {f.Bornes} borne(s)")
            .Should().BeEmpty();
    }

    // Convert rend 0 sur un compteur absent : un lecteur de compteurs bruts passe par LectureWmiBornee.Compteur.
    private static readonly Regex ConvertDirect = new(@"Convert\.To\w+\(");

    [Fact]
    public void Un_compteur_brut_absent_n_est_jamais_converti_en_zero()
    {
        var lecteurs = FichiersDuDepot.Sous(Path.Combine(Racine(), "src"), "*.cs")
            .Where(f => Path.GetFileName(f) != "LectureWmiBornee.cs")
            .Select(f => (Nom: Path.GetFileName(f), Texte: File.ReadAllText(f)))
            .Where(f => f.Texte.Contains("Win32_PerfRawData", StringComparison.Ordinal))
            .ToList();

        lecteurs.Should().HaveCountGreaterThanOrEqualTo(3, "témoin : veille, recensement par cœur et pression mémoire sont trouvés");
        lecteurs.Where(f => ConvertDirect.IsMatch(f.Texte)).Select(f => f.Nom).Should().BeEmpty();
        ConvertDirect.IsMatch("Convert.ToUInt64(o[n], CultureInfo.InvariantCulture)").Should().BeTrue();
    }

    [Fact]
    public void Le_crible_compte_les_requetes_et_les_bornes()
    {
        Requete.Matches("using var s = new System.Management.ManagementObjectSearcher(\"x\"); new ManagementClass(a, b, c);").Count.Should().Be(2);
        Borne.IsMatch("return LectureWmiBornee.Lire(() => 1, () => 0);").Should().BeTrue();
        Borne.IsMatch("=> LectureWmiBornee.Lire<(ulong N, ulong T)?>(() =>").Should().BeTrue();
        Borne.IsMatch("LectureWmiBornee.LireAutre(").Should().BeFalse();
    }
}
