using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les tables de noms du BIOS AMD : une carte et une version exactes, chaque réglage avec ses copies et leurs conditions de
/// visibilité, une empreinte d'usine par variable. Les tables connues sont relues ici, champ par champ.
/// </summary>
public class TablesBiosAmdTests
{
    private const string Guid = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";

    // Carte et version inventées.
    private static TableBiosAmd Table(string carte = "CARTE AMD FICTIVE", string version = "0042") =>
        new(carte, version, "image publique", [new VariableAmd("Setup", Guid, 4, new string('0', 64))], []);

    [Fact]
    public void La_carte_et_la_version_se_reconnaissent_a_l_egalite_exacte_jamais_par_contient()
    {
        var tables = new[] { Table() };

        TablesBiosAmd.Pour("CARTE AMD FICTIVE", "0042", tables).Should().NotBeNull();
        TablesBiosAmd.Pour(" CARTE AMD FICTIVE ", "0042 ", tables).Should().NotBeNull("les espaces de bord du SMBIOS ne changent pas la carte");
        TablesBiosAmd.Pour("CARTE AMD FICTIVE WIFI", "0042", tables).Should().BeNull("la variante WIFI n'a pas le même BIOS");
        TablesBiosAmd.Pour("CARTE AMD FICTIVE", "0043", tables).Should().BeNull();
        TablesBiosAmd.Pour("carte amd fictive", "0042", tables).Should().BeNull();
        TablesBiosAmd.Pour(null, "0042", tables).Should().BeNull();
    }

    [Fact]
    public void Chaque_table_connue_est_complete_et_ses_reglages_visibles_sont_dits()
    {
        foreach (var t in TablesBiosAmd.Connues)
        {
            t.Source.Should().NotBeNullOrWhiteSpace();
            TablesBiosAmd.Connues.Count(x => x.Carte == t.Carte && x.VersionBios == t.VersionBios).Should().Be(1);
            t.Variables.Should().OnlyContain(v => v.EmpreinteUsine.Length == 64 && v.EmpreinteUsine.All(Uri.IsHexDigit)
                                                  && v.EmpreinteUsine == v.EmpreinteUsine.ToLowerInvariant());
            foreach (var r in t.Reglages)
            {
                TablesBiosAmd.NomsReconnus.Should().Contain(r.Nom);
                r.Copies.Should().NotBeEmpty();
                // Plusieurs copies sans condition : impossible de savoir laquelle le joueur voit.
                if (r.Copies.Count > 1) r.Copies.Should().OnlyContain(c => c.Visible.Count > 0, $"{r.Nom} a plusieurs copies");
                foreach (var c in r.Copies)
                {
                    t.Variables.Should().Contain(v => v.Nom == c.Emplacement.VarName && v.Guid == c.Emplacement.VendorGuid && v.Taille == c.Emplacement.VarSize);
                    c.CheminMenu.Should().NotBeNullOrWhiteSpace();
                }
            }
        }
    }

    [Fact]
    public void Aucune_table_n_est_entree_sans_image_publique_extraite_ni_releve()
    {
        // Le jour où une table entre, ce test change avec elle et nomme sa source (GATES : aucune image AMD extraite).
        TablesBiosAmd.Connues.Should().BeEmpty();
    }

    [Fact]
    public void Le_constat_ne_parle_que_d_une_machine_AMD_reconnue_et_dit_non_nomme_sans_table()
    {
        var zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

        TablesBiosAmd.Juger(null, "PRO B760-P", "1.H0").Should().BeEmpty();
        var sans = TablesBiosAmd.Juger(zen4, "CARTE AMD FICTIVE", "0042").Single();
        sans.Statut.Should().Be(RepairStatus.Unknown);
        sans.ValeurCourante.Should().Be("CARTE AMD FICTIVE / 0042");
        TablesBiosAmd.Juger(zen4, "CARTE AMD FICTIVE", "0042", [Table()]).Single().Statut.Should().Be(RepairStatus.Observed);
    }
}
