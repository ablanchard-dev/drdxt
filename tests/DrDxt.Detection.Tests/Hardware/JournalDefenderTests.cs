using DrDxt.Detection.Hardware;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

// 07/10/2026 (D3) : les événements 1000 / 1001 / 1002 du journal de Defender reliés en analyses par leur identifiant.
public sealed class JournalDefenderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_debut_et_sa_fin_de_meme_identifiant_font_une_analyse_et_une_fin_arretee_se_dit()
    {
        var a = JournalDefender.Relier([
            (1000, T, "{A}"), (1001, T.AddMinutes(1), "{A}"),
            (1000, T.AddHours(1), "{B}"), (1002, T.AddHours(1).AddSeconds(30), "{B}"),
            (1000, T.AddHours(2), "{C}"),
            (1001, T.AddHours(3), "{D}"),
        ]);

        a.Should().HaveCount(3, "une fin sans début lu est ignorée");
        a[0].Should().Be(new DrDxt.Core.AnalyseDefender(T, T.AddMinutes(1), false));
        a[1].Arretee.Should().BeTrue();
        a[2].Fin.Should().BeNull("pas encore finie dans la fenêtre lue");
    }

    [Fact]
    public void La_fin_d_une_autre_analyse_n_est_jamais_prise()
        => JournalDefender.Relier([(1000, T, "{A}"), (1001, T.AddMinutes(1), "{B}")]).Single().Fin.Should().BeNull();
}
