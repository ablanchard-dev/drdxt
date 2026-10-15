using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un revert doit restaurer la valeur du profil qu'il a modifie.
///
/// Le piege, maintenant que l'ecriture DRS sait viser un profil de jeu : un tweak ecrit dans
/// « C:\Games\cod.exe », capture son cliche de revert sur le profil de base, et le retour
/// restaure une valeur qui n'a jamais rien a voir avec ce qu'il a change. Le jeu garde le
/// reglage, la machine en recoit un autre, et rien n'echoue.
///
/// Ce n'est pas une crainte theorique : le depot a deja paye exactement ce bug d'unite sur
/// l'undervolt ADLX (63e9c66).
///
/// La faute ne peut s'ecrire que dans un fichier — celui du tweak qui forme la paire
/// (op d'application, op inverse). Ce gate y regarde : dans un meme fichier, toutes les
/// constructions d'<c>ApplyNvApiDrsSettingOp</c> doivent nommer la meme portee.
///
/// Honnetement : aujourd'hui aucun tweak n'ecrit par jeu, donc ce gate passe sans effort.
/// Il n'est pas ecrit pour l'etat actuel, il est arme pour le premier qui le fera — et il
/// rougira le jour ou la paire sera dissymetrique, c'est-a-dire le seul jour ou il compte.
/// </summary>
public sealed class DrsRevertMatchesItsScopeGateTests
{
    // Le 3e argument de `new ApplyNvApiDrsSettingOp(id, value, PORTEE)` — l'EXPRESSION telle
    // qu'ecrite, pas sa valeur : deux constantes differentes qui vaudraient la meme chaine
    // resteraient deux intentions differentes, et c'est l'intention qu'on compare.
    private static readonly Regex DrsOp = new(
        @"new\s+ApplyNvApiDrsSettingOp\s*\(\s*[^,()]+,\s*[^,()]+,\s*([^,()]+?)\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void A_file_that_writes_a_DRS_setting_uses_ONE_scope_for_both_the_apply_and_its_revert()
    {
        var offenders = new List<string>();
        var filesSeen = 0;

        foreach (var (path, text) in SourceFiles())
        {
            var scopes = DrsOp.Matches(text)
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (scopes.Count == 0) continue;
            filesSeen++;

            if (scopes.Count > 1)
                offenders.Add($"{Path.GetFileName(path)} : {string.Join(" / ", scopes)}");
        }

        filesSeen.Should().BeGreaterThan(0,
            "si plus aucun fichier ne construit cette op, ce gate est vert pour rien et le motif "
            + "de recherche est casse — pas le code");

        offenders.Should().BeEmpty(
            "un revert doit restaurer la valeur du profil qu'il a modifie ; melanger le profil de "
            + "base et un profil de jeu dans un meme tweak restaure la mauvaise valeur en silence");
    }

    [Fact]
    public void The_gate_would_actually_catch_a_mismatched_pair()
    {
        // Controle POSITIF, parce que le test ci-dessus est vert sans effort aujourd'hui. Sans
        // celui-ci, une expression reguliere cassee donnerait la meme couleur qu'un depot sain.
        const string mismatched = """
            var apply = new ApplyNvApiDrsSettingOp(FrlFps, 197u, gamePath);
            var undo  = new ApplyNvApiDrsSettingOp(FrlFps, original, GlobalProfileName);
            """;

        DrsOp.Matches(mismatched)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Should().HaveCount(2, "deux portees differentes doivent etre VUES comme differentes");
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        var root = Path.Combine(RepoRoot(), "src");
        return FichiersDuDepot.Sous(root, "*.cs")
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => (p, File.ReadAllText(p)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }
}
