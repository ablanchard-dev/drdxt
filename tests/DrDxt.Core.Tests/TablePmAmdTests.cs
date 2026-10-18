using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// La table PM d'un Ryzen : une version connue, sur sa génération, à sa taille exacte ; et le bridage compté en partie.
public sealed class TablePmAmdTests
{
    private static readonly GenerationAmd.Ligne Zen3 = GenerationAmd.Table.First(l => l.Generation == "Zen 3" && l.ModeleMin == 0x21);

    // Une table 0x380805 (Vermeer, 0x8F0 octets), valeurs inventées : PPT 130 W sur 130, TDC 60 A sur 88, thermique 72 °C
    // sur 85, EDC 110 A sur 125.
    private static byte[] Vermeer(float ppt = 130, float temperature = 72)
    {
        var t = new byte[0x8F0];
        void Ecrire(int i, float v) => BitConverter.GetBytes(v).CopyTo(t, i * 4);
        Ecrire(0, 130); Ecrire(1, ppt); Ecrire(2, 88); Ecrire(3, 60); Ecrire(4, 85); Ecrire(5, temperature); Ecrire(8, 125); Ecrire(9, 110);
        return t;
    }

    [Fact]
    public void Une_version_connue_se_decode_sur_sa_generation_a_sa_taille_exacte()
    {
        TablePmAmd.Decoder(0x19, 0x21, 0x380805, Vermeer()).Should().Be(new LimitesAmd(130, 130, 60, 88, 110, 125, 72, 85));
        TablePmAmd.Decoder(0x19, 0x21, 0x380805, Vermeer().AsSpan(0, 0x8A4)).Should().BeNull("taille d'une autre version");
        TablePmAmd.Decoder(0x17, 0x71, 0x380805, Vermeer()).Should().BeNull("table Vermeer sur un Matisse");
    }

    [Fact]
    public void Une_version_devinee_ou_sans_source_publique_n_est_pas_lue()
    {
        TablePmAmd.Decoder(0x19, 0x21, 0x380905, new byte[0x5D0]).Should().BeNull("0x380905 est une supposition dans sa source");
        TablePmAmd.Decoder(0x19, 0x61, 0x540104, new byte[0x6A8]).Should().BeNull("aucune source publique pour Zen 4");
        TablePmAmd.Connues.Should().NotContain(v => v.Famille == 0x19 && v.Modele == 0x61);
        TablePmAmd.Connues.Should().NotContain(v => v.Famille == 0x1A);
    }

    [Fact]
    public void Une_valeur_non_plausible_rend_non_lu()
    {
        TablePmAmd.Decoder(0x19, 0x21, 0x380805, Vermeer(ppt: float.NaN)).Should().BeNull();
        TablePmAmd.Decoder(0x19, 0x21, 0x380805, Vermeer(temperature: 400)).Should().BeNull();
    }

    [Fact]
    public void Le_bridage_compte_97_pour_cent_de_la_limite_et_la_temperature_a_sa_limite_en_degres()
    {
        var b = BridageAmd.Depuis([
            new LimitesAmd(127, 130, 60, 88, 110, 125, 84, 85),
            new LimitesAmd(126, 130, 86, 88, 110, 125, 85, 85),
        ])!;

        b.Ppt.Should().Be(1, "127 W dépasse 97 % de 130 W, 126 W non");
        b.Tdc.Should().Be(1);
        b.Edc.Should().Be(0);
        b.Thermique.Should().Be(1, "84 °C sur 85 n'est pas la limite, même à 98,8 % de sa valeur");
        BridageAmd.Depuis([]).Should().BeNull();
    }

    // Le seuil de 97 % est un choix de DrDXT : les deux textes le disent comme tel, en français et en anglais.
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Le_seuil_de_97_pour_cent_est_dit_comme_un_choix_de_DrDXT(string langue)
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo(langue));
        foreach (var cle in new[] { "PMA.RienNeBride", "PMA.Bride" })
            Docteur.T(cle, 0, 0).Should().Contain("97").And.Contain("DrDXT");
    }

    [Fact]
    public void Le_constat_dit_non_lu_ou_ce_qui_bride()
    {
        TablePmAmd.Juger(null, null).Should().BeEmpty();
        TablePmAmd.Juger(Zen3, null).Single().Explication.Should().Be(Docteur.T("PMA.NonLue"));
        TablePmAmd.Juger(Zen3, new BridageAmd(100, 0, 1, 0, 0)).Single().Explication.Should().Be(Docteur.T("PMA.RienNeBride", 100));
        var bride = TablePmAmd.Juger(Zen3, new BridageAmd(100, 40, 0, 0, 10)).Single();
        bride.Explication.Should().Be(Docteur.T("PMA.Bride", string.Format(Docteur.Langue, "PPT {0:P0}, Tjmax {1:P0}", 0.4, 0.1), 100));
    }

    // Le docteur parle la langue de l'affichage de Windows ; une part suit sa phrase, pas les formats régionaux.
    [Fact]
    public void Les_parts_suivent_la_langue_du_docteur_et_non_les_formats_regionaux()
    {
        var avant = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            TablePmAmd.Juger(Zen3, new BridageAmd(100, 40, 0, 0, 10)).Single().ValeurCourante.Should().Contain("PPT 40%");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = avant; }
    }
}
