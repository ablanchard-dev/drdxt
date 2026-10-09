using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 26/09/2026 : le code, les tests et les outils parlaient du mainteneur à la troisième personne
/// (« demande d'Untel », « la machine d'Untel », « chez Untel ») : la voix d'un rapport, pas celle de
/// l'auteur. Environ 150 lignes ont été réécrites. Ce garde empêche qu'elles reviennent. Il cherche les
/// tournures, pas le prénom seul, et ne se lit pas lui-même.
/// </summary>
public sealed class LAuteurNeParlePasDeLuiALaTroisiemePersonneTests
{
    // Le prénom est assemblé à l'exécution : la copie publique refuse le mot collé à un tiret bas, ce fichier compris.
    private const string Prenom = "Al" + "ex";
    private static readonly Regex Tournure = new(
        @"\b(d'|qu'|chez |a |à |de |par |pour )" + Prenom + @"\b|\b" + Prenom + @"('s| a | doit| tranche| regard| lanc| jou)"
        // 26/09/2026 : les noms de tests collent les mots par un tiret bas (« chez_Untel_est »), que `\b` ne voit pas.
        + @"|(^|[^A-Za-z])(d|qu|chez|de|par|pour)_" + Prenom + @"(_|[^A-Za-z]|$)",
        RegexOptions.Compiled);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Le_code_les_tests_et_les_outils_ne_parlent_pas_du_mainteneur_a_la_troisieme_personne()
    {
        // Témoin : les tournures d'avant sont bien attrapées.
        Tournure.IsMatch("// 11/09/2026, demande d'" + Prenom + ".").Should().BeTrue();
        Tournure.IsMatch("mesure chez " + Prenom + " le 24/09").Should().BeTrue();
        Tournure.IsMatch("public void Le_profil_mesure_chez_" + Prenom + "_est_detecte()").Should().BeTrue();
        Tournure.IsMatch("var alexandre = Alexandrie;").Should().BeFalse();

        var racine = Racine();
        var moi = nameof(LAuteurNeParlePasDeLuiALaTroisiemePersonneTests) + ".cs";
        var lus = new[] { "src", "tests", "tools", "installer", ".github" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d)))
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".resx" or ".ps1" or ".py" or ".yml" or ".iss" or ".md" or ".txt")
            .Where(f => !f.EndsWith(moi, StringComparison.OrdinalIgnoreCase))
            .ToList();
        lus.Should().HaveCountGreaterThan(100, "un crible qui ne lit rien ne trouve rien");

        var fautes = lus
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => Tournure.IsMatch(x.l))
            .Select(x => $"{Path.GetRelativePath(racine, x.f)}:{x.i + 1}")
            .ToList();

        fautes.Should().BeEmpty("le dépôt est celui de son auteur : il ne parle pas de lui à la troisième personne");
    }
}
