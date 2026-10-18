using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le schéma fermé des tables que DrDXT peut recevoir sans changer de binaire : une clé inconnue, une ligne sans
/// source ou un champ hors forme refusent le contenu entier. Un contenu de mise à jour qui transporterait du code ou un
/// fichier à lancer n'a aucune place où se loger.
/// </summary>
public sealed class TablesDeDonneesTests
{
    private const string Regle =
        """{"id":"exemple-1","kb":"KB5063878","fr":"Phrase.","en":"Sentence.","source":"https://learn.microsoft.com/x","releve":"2026-10-16"}""";

    private static string Contenu(string radar = "[" + Regle + "]", string extra = "")
        => $$"""{"publie":"2026-10-17","radar":{{radar}}{{extra}}}""";

    [Fact]
    public void Un_contenu_conforme_se_lit_ligne_par_ligne()
    {
        var lu = TablesDeDonnees.Lire(Contenu(), version: 3);

        lu.IsOk.Should().BeTrue(lu.Error?.Message);
        lu.Value!.Version.Should().Be(3);
        lu.Value.Publie.Should().Be(new DateOnly(2026, 10, 17));
        var r = lu.Value.Radar.Should().ContainSingle().Subject;
        r.Kb.Should().Be("KB5063878");
        r.Source.Should().Be("https://learn.microsoft.com/x");
        r.Releve.Should().Be(new DateOnly(2026, 10, 16));
    }

    [Theory]
    // Une clé que le schéma ne connaît pas : un script, un exécutable, une liste de fichiers.
    [InlineData(""","script":"Start-Process cmd" """)]
    [InlineData(""","fichiers":[{"nom":"a.exe","octets":"TVqQAAMAAAAEAAAA"}]""")]
    [InlineData(""","commande":"reg add HKLM" """)]
    public void Une_cle_inconnue_a_la_racine_refuse_tout_le_contenu(string extra)
    {
        TablesDeDonnees.Lire(Contenu(extra: extra), 1).IsOk.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"id":"a","kb":"KB5063878","fr":"x","en":"y","releve":"2026-10-16"}""")] // sans source
    [InlineData("""{"id":"a","kb":"KB5063878","fr":"x","en":"y","source":"http://exemple.org","releve":"2026-10-16"}""")] // en clair
    [InlineData("""{"id":"a","kb":"KB5063878","fr":"x","en":"y","source":"https://exemple.org"}""")] // sans date de relevé
    [InlineData("""{"id":"a","fr":"x","en":"y","source":"https://exemple.org","releve":"2026-10-16"}""")] // ne vise rien
    [InlineData("""{"id":"a","kb":"5063878","fr":"x","en":"y","source":"https://exemple.org","releve":"2026-10-16"}""")] // KB mal formé
    [InlineData("""{"id":"a","kb":"KB5063878","fr":"x","source":"https://exemple.org","releve":"2026-10-16"}""")] // une langue manque
    [InlineData("""{"id":"a","kb":"KB5063878","fr":"x","en":"y","source":"https://exemple.org","releve":"2026-10-16","executer":"x"}""")]
    [InlineData("""{"id":"a","carte":"NVIDIA","piloteDepuis":"32.0.abc","fr":"x","en":"y","source":"https://exemple.org","releve":"2026-10-16"}""")]
    public void Une_ligne_hors_schema_refuse_tout_le_contenu(string ligne)
    {
        TablesDeDonnees.Lire(Contenu("[" + Regle + "," + ligne + "]"), 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Deux_lignes_au_meme_identifiant_refusent_le_contenu()
    {
        TablesDeDonnees.Lire(Contenu("[" + Regle + "," + Regle + "]"), 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Un_texte_avec_un_caractere_de_controle_est_refuse()
    {
        var ligne = Regle.Replace("Phrase.", "Phrase\\u0007");
        TablesDeDonnees.Lire(Contenu("[" + ligne + "]"), 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Ce_qui_n_est_pas_du_JSON_est_refuse_sans_lever()
    {
        TablesDeDonnees.Lire("MZ\u0090\u0000", 1).IsOk.Should().BeFalse();
        TablesDeDonnees.Lire("", 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Les_tables_embarquees_ont_la_version_zero_et_chaque_ligne_porte_sa_source()
    {
        TablesDeDonnees.Embarquees.Version.Should().Be(0);
        TablesDeDonnees.Embarquees.Radar.Where(r =>
            !r.Source.StartsWith("https://", StringComparison.Ordinal) || r.Releve < new DateOnly(2020, 1, 1)).Should().BeEmpty();
    }

    [Fact]
    public void Les_donnees_en_service_partent_des_tables_embarquees_et_ne_reculent_jamais()
    {
        using var _ = DonneesEnService.Portee();
        DonneesEnService.Courantes.Version.Should().Be(0);

        var v3 = TablesDeDonnees.Lire(Contenu(), 3).Value!;
        var v2 = TablesDeDonnees.Lire(Contenu(), 2).Value!;
        DonneesEnService.Installer(v3).Should().BeTrue();
        DonneesEnService.Installer(v2).Should().BeFalse("une version plus ancienne n'en remplace jamais une plus récente");
        DonneesEnService.Courantes.Version.Should().Be(3);
    }
}
