using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Hardware;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

// Les bibliothèques Steam lues dans libraryfolders.vdf, et le lien PCIe d'un SSD NVMe jugé.
public sealed class StockageDesJeuxTests
{
    [Fact]
    public void Les_bibliotheques_steam_se_lisent_antislashs_rendus()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\Program Files (x86)\\Steam\"\n\t}\n"
                         + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\SteamLibrary\"\n\t}\n}";
        DossiersDeJeux.CheminsSteam(vdf).Should().Equal(@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary");
    }

    [Fact]
    public void Un_ssd_sur_moins_de_lignes_que_les_siennes_se_dit_avec_le_geste()
    {
        var c = BranchementDeLaCarte.JugerLienSsd(new("SSD", 4, 2, 4, 4)).Single();
        c.Statut.Should().Be(RepairStatus.NonDefault, "un emplacement câblé x2 par la carte mère est un choix de conception");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Une_generation_sous_le_maximum_est_dite_sans_jugement_et_un_lien_plein_est_attendu()
    {
        BranchementDeLaCarte.JugerLienSsd(new("SSD", 4, 4, 4, 3)).Single().Statut.Should().Be(RepairStatus.Observed);
        BranchementDeLaCarte.JugerLienSsd(new("SSD", 4, 4, 4, 4)).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
        BranchementDeLaCarte.JugerLienSsd(new("SSD", 0, 0, 0, 0)).Should().BeEmpty("non lu : rien n'est dit");
    }
}
