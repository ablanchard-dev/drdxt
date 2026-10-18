using DrDxt.Application.Support;
using FluentAssertions;

namespace DrDxt.Application.Tests.Support;

// « Quoi de neuf » se tire du CHANGELOG livré : les points pour le joueur, des versions non vues, une fois.
public sealed class NotesDeVersionTests
{
    private const string Journal = """
        # Changements

        ## 0.9.2 (20/10/2026)

        Pour le joueur :
        - Les écrans bleus se comptent par trois sources.
        - La santé des SSD NVMe est lue,
          croisée avec les arrêts brutaux.

        Pour les contributeurs :
        - Un test de plus.

        ## 0.9.1 (16/10/2026)

        Pour le joueur :
        - Une seule App par session.

        ## 0.9.0 (09/10/2026)

        Pour le joueur :
        - Première version.
        """;

    [Fact]
    public void Seuls_les_points_pour_le_joueur_sont_repris()
    {
        var s = NotesDeVersion.Sections(Journal, Fr);

        s.Select(x => x.Version).Should().Equal("0.9.2", "0.9.1", "0.9.0");
        s[0].Points.Should().Equal("Les écrans bleus se comptent par trois sources.", "La santé des SSD NVMe est lue, croisée avec les arrêts brutaux.");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d!.FullName, "DrDXT.sln"))) d = d.Parent;
        return d.FullName;
    }

    // La section à venir d'un vrai CHANGELOG (fichier, titre, puces).
    private static (string Texte, int Puces) AVenir(string langue)
    {
        var (fichier, titre) = langue == "fr-FR" ? ("CHANGELOG.md", "## Non publié") : ("CHANGELOG.en.md", "## Unreleased");
        fichier.Should().Be(NotesDeVersion.Ressource(System.Globalization.CultureInfo.GetCultureInfo(langue)));
        var texte = File.ReadAllText(Path.Combine(Racine(), fichier)).Replace("\r\n", "\n");
        var debut = texte.IndexOf(titre, StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, "témoin : la section à venir existe");
        var fin = texte.IndexOf("\n## ", debut, StringComparison.Ordinal);
        var aVenir = texte[debut..(fin < 0 ? texte.Length : fin)].Replace(titre, "## 9.9.9");
        return (aVenir, aVenir.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    // La section à venir du vrai CHANGELOG, une fois numérotée, montre chacun de ses points dans sa langue : un groupe écrit
    // comme une ligne « Titre : » couperait le « Quoi de neuf » après le premier groupe, sans que rien ne le dise.
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Le_vrai_CHANGELOG_montre_chaque_point_de_la_prochaine_version(string langue)
    {
        var (aVenir, puces) = AVenir(langue);

        var section = NotesDeVersion.Sections(aVenir, System.Globalization.CultureInfo.GetCultureInfo(langue)).Single();

        puces.Should().BeGreaterThan(0);
        section.Points.Should().HaveCount(puces, "chaque point de la prochaine version arrive au « Quoi de neuf »");
    }

    [Fact]
    public void Les_deux_langues_annoncent_autant_de_changements()
        => AVenir("en-US").Puces.Should().Be(AVenir("fr-FR").Puces, "une ligne ajoutée d'un seul côté manquerait à l'autre langue");

    // À la publication, la section à venir prend son numéro des deux côtés à la fois : numérotée d'un seul côté, une langue
    // montrerait la nouvelle version et l'autre rien.
    [Fact]
    public void Les_deux_langues_sont_numerotees_ensemble()
    {
        var fr = File.ReadAllText(Path.Combine(Racine(), "CHANGELOG.md"));
        var en = File.ReadAllText(Path.Combine(Racine(), "CHANGELOG.en.md"));

        en.Contains("## Unreleased", StringComparison.Ordinal)
            .Should().Be(fr.Contains("## Non publié", StringComparison.Ordinal), "la section à venir existe des deux côtés, ou d'aucun");
        if (NotesDeVersion.Sections(en, System.Globalization.CultureInfo.GetCultureInfo("en-US")).FirstOrDefault() is { } derniere)
        {
            var enFrancais = NotesDeVersion.Sections(fr, Fr).First();
            derniere.Version.Should().Be(enFrancais.Version, "la dernière version publiée est la même");
            derniere.Points.Should().HaveCount(enFrancais.Points.Count, "elle annonce autant de changements dans les deux langues");
        }
    }

    // L'App lit la ressource que NotesDeVersion.Ressource nomme : un nom qui diverge laisserait le bloc muet.
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void L_App_embarque_le_CHANGELOG_de_chaque_langue(string langue)
    {
        var nom = NotesDeVersion.Ressource(System.Globalization.CultureInfo.GetCultureInfo(langue));
        File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.App", "DrDxt.App.csproj"))
            .Should().Contain($"Include=\"..\\..\\{nom}\" LogicalName=\"{nom}\"");
    }

    [Fact]
    public void Seules_les_versions_non_vues_reviennent_et_le_premier_lancement_montre_la_version_installee()
    {
        var s = NotesDeVersion.Sections(Journal, Fr);

        NotesDeVersion.NonVues(s, "0.9.0", "0.9.2").Select(x => x.Version).Should().Equal("0.9.2", "0.9.1");
        NotesDeVersion.NonVues(s, "0.9.2", "0.9.2").Should().BeEmpty();
        NotesDeVersion.NonVues(s, null, "0.9.1").Select(x => x.Version).Should().Equal("0.9.1");
    }

    [Fact]
    public void Une_version_vue_illisible_vaut_un_premier_lancement_au_lieu_de_faire_tomber_l_App()
    {
        // Lu au démarrage de l'App : une exception ici l'empêchait de s'ouvrir.
        NotesDeVersion.VersionVue("\"0.9.1\"").Should().Be("0.9.1");
        NotesDeVersion.VersionVue("{\"pas\":\"une chaine\"}").Should().BeNull();
        NotesDeVersion.VersionVue("tronqu").Should().BeNull();
        NotesDeVersion.VersionVue(null).Should().BeNull();
    }

    private const string SansJoueur = """
        ## 0.9.3 (25/10/2026)

        Pour les contributeurs :
        - Un test de plus.
        - Une garde.
        """;

    [Fact]
    public void Une_section_sans_points_pour_le_joueur_ne_montre_rien()
    {
        NotesDeVersion.Sections(SansJoueur, Fr).Single().Points.Should().BeEmpty("les points des contributeurs ne sont jamais repris");
        NotesDeVersion.AMontrer(SansJoueur, "0.9.2", "0.9.3", Fr).Should().BeEmpty("une section sans point ne montre pas même son titre");
    }

    [Fact]
    public void Un_joueur_anglais_lit_le_CHANGELOG_anglais_et_jamais_le_francais()
    {
        const string journalEn = """
            # Changes

            ## 0.9.2 (20/10/2026)

            For the player:
            - Blue screens counted from three sources.

            For contributors:
            - One more test.
            """;
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        NotesDeVersion.Ressource(Fr).Should().Be("CHANGELOG.md");
        NotesDeVersion.Ressource(en).Should().Be("CHANGELOG.en.md");
        NotesDeVersion.AMontrer(journalEn, "0.9.0", "0.9.2", en).Single().Points.Should().Equal("Blue screens counted from three sources.");
        NotesDeVersion.AMontrer(Journal, "0.9.0", "0.9.2", en).Should().BeEmpty("le texte français ne se montre pas à un joueur anglais");
        NotesDeVersion.AMontrer(Journal, "0.9.0", "0.9.2", Fr).Select(x => x.Version).Should().Equal("0.9.2", "0.9.1");
    }

    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
}
