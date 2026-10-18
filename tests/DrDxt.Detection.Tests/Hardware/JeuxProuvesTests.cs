using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// Un dossier de jeu prouvé par l'exécutable du jeu, la version tirée du fichier de build de Battle.net.
public sealed class JeuxProuvesTests
{
    // Forme relevée sur la machine de référence (colonnes réduites).
    private const string BuildInfo = "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|Version!STRING:0|Product!STRING:0\r\n"
        + "eu|1|06eaf9bf|3.14.2.28787707|auks\r\n";

    [Fact]
    public void La_version_est_celle_de_la_branche_active_du_fichier_de_build()
    {
        JeuxProuves.VersionBattleNet(BuildInfo).Should().Be("3.14.2.28787707");
        JeuxProuves.VersionBattleNet("Branch!STRING:0|Active!DEC:1|Version!STRING:0\nus|0|1.0\neu|1|2.0").Should().Be("2.0");
        JeuxProuves.VersionBattleNet("Branch!STRING:0|Active!DEC:1\neu|1").Should().BeNull();
        JeuxProuves.VersionBattleNet(null).Should().BeNull();
    }

    // Notre règle : une seule liste de jeux, et un nom exact. Un éditeur de code ou un nom qui contient « cod » ne prouve rien.
    [Theory]
    [InlineData(@"_retail_\cod.exe", @"_retail_\cod.exe")]
    [InlineData(@"bin\Code.exe", null)]
    [InlineData(@"tools\applix.exe", null)]
    [InlineData(@"Call of Duty Launcher.exe", null)]
    public void Seul_le_nom_exact_d_un_jeu_connu_prouve_le_dossier(string fichier, string? attendu)
        => JeuxProuves.ExecutableConnu([fichier]).Should().Be(attendu);

    [Fact]
    public void Le_constat_dit_les_jeux_prouves_et_les_dossiers_non_prouves()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = JeuxProuves.Juger([
            new("Battle.net", @"C:\Program Files (x86)\Call of Duty", @"_retail_\cod.exe", "3.14.2.28787707"),
            new("Battle.net", @"C:\Program Files (x86)\Diablo IV", null, null)]);
        c.Explication.Should().Contain(@"« Call of Duty » (Battle.net) : _retail_\cod.exe est là, version 3.14.2.28787707.")
            .And.Contain("Dossiers non prouvés").And.Contain("Diablo IV");
        JeuxProuves.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
    }
}
