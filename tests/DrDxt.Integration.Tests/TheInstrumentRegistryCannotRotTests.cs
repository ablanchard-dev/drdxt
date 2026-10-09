using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un plancher de bruit sans date est une opinion.
///
/// `docs/INSTRUMENTS.md` existe parce que ce projet a cite pendant un jour un plancher de bruit de
/// 72,6 % sur un instrument qui, relance, rend 0,122 %. Le chiffre n'avait pas ete re-mesure : il
/// avait ete recopie d'un journal, deux fois, par moi.
///
/// Ce controle rend cette faute impossible a repeter en silence. Chaque ligne du registre doit
/// porter :
///   • un plancher chiffre (`1,86 %`) ou l'aveu explicite `UNKNOWN` -- jamais un mot rassurant ;
///   • le nombre de passes `n` sur lequel il a ete calcule, et `0` passe interdit un chiffre ;
///   • une DATE de mesure au format ISO, ou la mention `jamais mesure` ;
///   • un verdict pris dans une liste fermee.
///
/// ══ ce qu'il ne fait pas ══
/// Il ne relance pas les instruments : un test unitaire qui benche la machine d'integration
/// mesurerait cette machine-la, pas celle du client, et rendrait la CI dependante de sa charge.
/// La re-mesure est un geste de la REVUE, inscrit dans le contrat de boucle. Ce test garde la
/// FORME du registre ; c'est la revue qui garde sa FRAICHEUR.
/// </summary>
public sealed class TheInstrumentRegistryCannotRotTests
{
    private static readonly Regex Row = new(@"^\|\s*(I-\d+)\s*\|(.+)\|\s*$", RegexOptions.Multiline);
    private static readonly Regex Percent = new(@"^\d+(?:[.,]\d+)?\s*%$");
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");

    private static readonly string[] Verdicts =
        { "UTILISABLE", "HORS SERVICE", "HERITE", "PERIME" };

    // Le registre en portait 5 le 06/09/2026. Le vider ne doit pas etre une facon de passer.
    private const int RowsAtGateWriting = 5;

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

    private sealed record Instrument(string Id, string Floor, string Passes, string MeasuredOn, string Verdict);

    private static List<Instrument> Registry()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "INSTRUMENTS.md"));

        return Row.Matches(doc).Select(m =>
        {
            // Colonnes apres l'id : instrument | ce qu'il mesure | plancher | n | mesure le | verdict
            var c = m.Groups[2].Value.Split('|').Select(x => x.Trim()).ToArray();
            c.Length.Should().Be(6, "la ligne {0} doit porter exactement 7 colonnes", m.Groups[1].Value);
            return new Instrument(m.Groups[1].Value, c[2], c[3], c[4], c[5]);
        }).ToList();
    }

    [Fact]
    public void EveryInstrumentStatesItsNoiseFloorOrAdmitsItDoesNotKnow()
    {
        var rows = Registry();
        rows.Should().HaveCountGreaterThanOrEqualTo(
            RowsAtGateWriting, "vider le registre ne doit pas etre une facon de passer ce controle");

        foreach (var i in rows)
            (Percent.IsMatch(i.Floor) || i.Floor == "UNKNOWN").Should().BeTrue(
                "{0} doit porter un pourcentage ou l'aveu UNKNOWN, pas « {1} »", i.Id, i.Floor);
    }

    [Fact]
    public void ANumberedFloorCarriesTheDateAndThePassCountThatProducedIt()
    {
        foreach (var i in Registry().Where(x => Percent.IsMatch(x.Floor)))
        {
            IsoDate.IsMatch(i.MeasuredOn).Should().BeTrue(
                "{0} chiffre son plancher : il doit dire QUAND (AAAA-MM-JJ), pas « {1} »",
                i.Id, i.MeasuredOn);

            int.TryParse(i.Passes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                .Should().BeTrue("{0} doit dire sur combien de passes, pas « {1} »", i.Id, i.Passes);
            n.Should().BeGreaterThan(0,
                "{0} annonce un plancher calcule sur zero passe : c'est une opinion", i.Id);
        }
    }

    [Fact]
    public void AnInstrumentNeverMeasuredHereMayNotClaimANumber()
    {
        // La faute exacte qui a cree ce fichier : un chiffre herite d'un journal, presente comme
        // une mesure. Un instrument qu'on n'a pas lance n'a pas de plancher, il a un UNKNOWN.
        foreach (var i in Registry().Where(x => x.Passes == "0"))
            i.Floor.Should().Be("UNKNOWN",
                "{0} n'a jamais ete lance ici : il ne peut pas porter de chiffre", i.Id);
    }

    [Fact]
    public void TheVerdictComesFromAClosedList()
    {
        foreach (var i in Registry())
            Verdicts.Any(v => i.Verdict.StartsWith(v, StringComparison.Ordinal)).Should().BeTrue(
                "{0} porte le verdict « {1} », hors de la liste fermee", i.Id, i.Verdict);
    }
}
