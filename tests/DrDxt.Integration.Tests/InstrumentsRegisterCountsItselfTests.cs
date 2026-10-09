using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un chiffre ecrit a la main est une dette — y compris dans le fichier qui l'enseigne.
///
/// Constate le 07/09/2026 : `docs/INSTRUMENTS.md` concluait « **5 instruments. 4 UTILISABLES** »
/// alors que son propre registre en listait **six, tous utilisables**. I-06 avait ete ajoute le
/// 06/09 sans que le total soit refait. Le fichier dont le sujet est « ce qu'on peut prouver » se
/// trompait sur le nombre de choses qu'il prouve.
///
/// Ce n'est pas une coquille : c'est la forme exacte du defaut que ce depot traque. Un total
/// recopie a la main pourrit a l'entree suivante, et personne ne relit un chiffre qui a l'air
/// plausible. Le test le RECALCULE depuis le tableau, donc il ne peut plus mentir plus longtemps
/// que le temps d'une execution de suite.
/// </summary>
public sealed class InstrumentsRegisterCountsItselfTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static string Doc() => File.ReadAllText(Path.Combine(RepoRoot(), "docs", "INSTRUMENTS.md"));

    /// <summary>Les lignes du registre : `| I-01 | ... |`.</summary>
    private static IReadOnlyList<string> Registre() =>
        Regex.Matches(Doc(), @"^\| (I-\d+) \|.*$", RegexOptions.Multiline)
            .Select(m => m.Value).ToList();

    [Fact]
    public void Le_total_annonce_est_celui_que_le_registre_contient()
    {
        var attendu = Registre().Count;

        attendu.Should().BeGreaterThan(0, "sans registre, ce test serait vert pour rien");
        Doc().Should().Contain($"**{attendu} instruments",
            "le total doit se recalculer depuis le tableau, jamais se recopier");
    }

    [Fact]
    public void Le_compte_des_UTILISABLES_est_celui_du_registre_aussi()
    {
        var utilisables = Registre().Count(l => l.Contains("UTILISABLE", StringComparison.Ordinal));

        Doc().Should().Contain($"{utilisables} UTILISABLES",
            "le second chiffre s'est perime en meme temps que le premier, et pour la meme raison");
    }

    [Fact]
    public void Chaque_instrument_porte_un_verdict_et_une_date_de_mesure()
    {
        // La garde qui empeche d'ajouter une ligne sans la mesurer : un instrument sans date ni
        // verdict est une promesse, pas un instrument.
        foreach (var ligne in Registre())
        {
            ligne.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}", $"une date manque : {ligne[..Math.Min(40, ligne.Length)]}");
            ligne.Should().ContainAny("UTILISABLE", "HORS SERVICE", "INCONNU");
        }
    }
}
