using System.Globalization;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using DrDxt.Measurement;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La ligne qui dit le resultat de la mesure affichait son propre code.
//
// L'ecran de resultat liait `VerdictKey = verdict?.SummaryKey`, c'est-a-dire l'une des
// trois chaines que `AbComparer.Compare` produit -- `Scan.Ab.MeasurableGain`,
// `Scan.Ab.NoMeasurableGain`, `Scan.Ab.Regression`.
//
// Aucune des trois n'existait dans `Strings.resx` ni dans `Strings.fr.resx`. Et le
// contrat de `ResxLocalizer` est ecrit noir sur blanc : « Missing keys return the key
// itself — never null, never an exception. » Rien ne leve, rien ne rougit : l'ecran de
// resultat affichait litteralement `Scan.Ab.NoMeasurableGain` a la
// place de la phrase qui dit ce que la session a mesure.
//
// C'est le defaut qui ne leve rien, sur la seule ligne pour laquelle on lance un A/B.
//
// Ce crible ne recopie pas les trois chaines : il les fait PRODUIRE par le comparateur,
// sur des mesures qui declenchent chacun des trois verdicts. Une quatrieme issue ajoutee
// demain avec une clef neuve sortira ici, non traduite, dans les deux langues annoncees
// par le produit (`SatelliteResourceLanguages` = en;fr).
// -----------------------------------------------------------------------
public sealed class LeVerdictDeLaMesureEstUnePhraseTests
{
    private static BenchmarkResult Bench(double avg, double moe) =>
        new(avg, avg * 0.7, avg * 0.5, new List<double>(), null, moe, 5);

    /// <summary>Les trois verdicts, produits par le comparateur et non recopies.</summary>
    public static TheoryData<string, string> LesTroisVerdicts()
    {
        var gain = AbComparer.Compare(Bench(100, 1), Bench(110, 1));
        var sansGain = AbComparer.Compare(Bench(100, 3), Bench(103, 3));
        var regression = AbComparer.Compare(Bench(110, 1), Bench(100, 1));

        var donnees = new TheoryData<string, string>();
        foreach (var cle in new[] { gain.SummaryKey, sansGain.SummaryKey, regression.SummaryKey })
        {
            donnees.Add(cle, "en");
            donnees.Add(cle, "fr");
        }
        return donnees;
    }

    [Fact]
    public void Le_comparateur_rend_bien_trois_verdicts_distincts()
    {
        // Temoin de contenu : sans lui, le crible pourrait verifier trois fois la meme clef.
        var cles = new[]
        {
            AbComparer.Compare(Bench(100, 1), Bench(110, 1)).SummaryKey,
            AbComparer.Compare(Bench(100, 3), Bench(103, 3)).SummaryKey,
            AbComparer.Compare(Bench(110, 1), Bench(100, 1)).SummaryKey,
        };

        cles.Should().OnlyHaveUniqueItems();
        cles.Should().AllSatisfy(c => c.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Une_clef_absente_est_bien_rendue_telle_quelle()
    {
        // Temoin inverse : c'est ce comportement qui rend le defaut invisible. Sans lui,
        // on ne saurait pas que « la traduction manque » et « la clef s'affiche » sont
        // le meme evenement.
        new ResxLocalizer().Get("Cette.Clef.N.Existe.Pas")
            .Should().Be("Cette.Clef.N.Existe.Pas");
    }

    [Theory]
    [MemberData(nameof(LesTroisVerdicts))]
    public void Chaque_verdict_A_B_est_traduit_dans_les_deux_langues_du_produit(string cle, string langue)
    {
        var texte = new ResxLocalizer().Get(cle, CultureInfo.GetCultureInfo(langue));

        texte.Should().NotBe(cle,
            $"la ligne de verdict du tableau de bord affiche cette clef telle quelle en « {langue} ». " +
            "C'est la phrase qui dit au client ce que la mesure a trouve : elle ne peut pas etre un code");
        texte.Should().NotBeNullOrWhiteSpace();
    }
}
