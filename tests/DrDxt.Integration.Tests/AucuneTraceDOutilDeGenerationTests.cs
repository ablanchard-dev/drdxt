using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 04/10/2026 : la règle « aucune signature d'outil de génération » ne vivait que dans une consigne de session,
/// sans garde. Relevé ce jour : zéro dans les fichiers suivis et zéro dans les 1 690 messages de commit. Ce test
/// garde les deux à zéro. Il ne se lit pas lui-même (il contient les motifs).
/// </summary>
public sealed class AucuneTraceDOutilDeGenerationTests
{
    private static readonly Regex Signature = new(
        @"co-authored-by\s*:|generated with \[?claude|noreply@anthropic\.com|claude\.com/claude-code|" + "\U0001F916",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_fichier_du_depot_ne_porte_de_signature_d_outil()
    {
        Signature.IsMatch("Co-Authored-By: Quelqu'un <x@y.z>").Should().BeTrue("témoin");
        Signature.IsMatch("\U0001F916 Generated with [Claude Code](https://claude.com/claude-code)").Should().BeTrue("témoin");
        Signature.IsMatch("// auteur : la mesure du 04/10").Should().BeFalse("témoin");

        var racine = Racine();
        var moi = nameof(AucuneTraceDOutilDeGenerationTests) + ".cs";
        var lus = new[] { "src", "tests", "tools", "installer", "docs", ".github" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d)))
            .Concat(Directory.EnumerateFiles(racine, "*.md", SearchOption.TopDirectoryOnly))
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".resx" or ".ps1" or ".py" or ".yml" or ".iss" or ".md" or ".txt")
            .Where(f => !f.EndsWith(moi, StringComparison.OrdinalIgnoreCase))
            .ToList();
        lus.Should().HaveCountGreaterThan(100, "un crible qui ne lit rien ne trouve rien");

        var fautes = lus
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => Signature.IsMatch(x.l))
            .Select(x => $"{Path.GetRelativePath(racine, x.f)}:{x.i + 1}")
            .ToList();
        fautes.Should().BeEmpty();
    }

    [Fact]
    public void Aucun_message_de_commit_ne_porte_de_signature_d_outil()
    {
        var racine = Racine();
        // Un dépôt sans .git (archive source) n'a pas d'historique à juger ; avec .git, git doit répondre.
        if (!Directory.Exists(Path.Combine(racine, ".git"))) return;

        var psi = new ProcessStartInfo("git", "log --format=%H%n%B")
        {
            WorkingDirectory = racine,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var git = Process.Start(psi)!;
        var lignes = git.StandardOutput.ReadToEnd().Split('\n');
        git.WaitForExit();
        git.ExitCode.Should().Be(0);
        // 09/10/2026 : le dépôt public part d'un seul commit, et la CI clone en profondeur 1 ; on exige seulement que git ait
        // rendu au moins un commit (une sortie vide ne prouverait rien), pas plus de mille lignes.
        lignes.Count(l => l.Length == 40 && l.All(Uri.IsHexDigit)).Should().BeGreaterThan(0, "git a rendu au moins un commit");

        lignes.Where(l => Signature.IsMatch(l)).Should().BeEmpty();
    }
}
