using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// La table des défauts reconnus par les fabricants : chaque ligne sourcée et datée, une famille inconnue jamais supposée.
public sealed class DefautsConnusTests
{
    [Theory]
    [InlineData("4B2QJXD7", true)]
    [InlineData("7B2QJXD7", false)]
    [InlineData("8B2QJXD7", false)]
    [InlineData("5B2QGXA7", null)]   // autre famille (980 PRO)
    [InlineData(null, null)]
    public void Un_firmware_se_compare_seulement_dans_sa_famille(string? firmware, bool? precede)
        => DefautsConnus.Precede(firmware, "7B2QJXD7").Should().Be(precede);

    [Fact]
    public void Un_990_PRO_au_firmware_ancien_est_signale_avec_son_bulletin()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = DefautsConnus.Juger([("Samsung SSD 990 PRO 2TB", "4B2QJXD7"), ("WD_BLACK SN850X", "620361WD")]).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("semiconductor.samsung.com").And.Contain("16/10/2026").And.Contain("n'écrit jamais");
    }

    // Notre règle : une entrée sans adresse ni date de relevé n'entre pas dans la table.
    [Fact]
    public void Chaque_ligne_porte_son_adresse_et_sa_date_de_releve()
        => DefautsConnus.Table.Should().OnlyContain(l => l.Source.StartsWith("https://") && l.Releve.Year >= 2026
            && Docteur.Chercher(l.Phrase) != null);

    [Fact]
    public void Liste_non_lue_est_dite_non_lue()
        => DefautsConnus.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);
}
