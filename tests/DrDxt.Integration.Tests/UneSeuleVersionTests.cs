using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Une seule source de version.
///
/// Deux sources (des litteraux dans `build-installer.ps1` et `DrDXT.iss`, et la version de l'assembly)
/// montreraient deux numeros au client, et la regle de non-retrogradation des mises a jour comparerait
/// le mauvais. La version vit dans `Directory.Build.props` ; le script la lit, la passe au publish
/// et a l'installeur, et l'installeur refuse de compiler sans elle.
/// </summary>
public sealed class UneSeuleVersionTests
{
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

    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RepoRoot(), .. chemin]));

    [Fact]
    public void La_version_vit_dans_Directory_Build_props()
    {
        Lire("Directory.Build.props").Should().MatchRegex(@"<Version>\d+\.\d+\.\d+</Version>");
    }

    [Fact]
    public void Le_script_d_installeur_n_ecrit_aucune_version_en_dur_et_la_passe_au_publish()
    {
        var script = Lire("installer", "build-installer.ps1");

        Regex.IsMatch(script, @"'\d+\.\d+\.\d+'").Should().BeFalse("un litteral est une seconde source");
        script.Should().Contain("Directory.Build.props");
        script.Should().Contain("-p:Version=$Version", "l'assembly et l'installeur doivent porter le meme numero");
    }

    [Fact]
    public void L_installeur_refuse_de_compiler_sans_la_version()
    {
        var iss = Lire("installer", "DrDXT.iss");

        Regex.IsMatch(iss, @"#define\s+AppVer\s+""").Should().BeFalse("un repli est une seconde source");
        iss.Should().Contain("#error");
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.fr.resx")]
    public void Le_pied_de_page_ne_porte_aucun_numero_en_dur(string fichier)
    {
        // Une troisieme source possible : un « Version 1.0.0 » ecrit dans le texte du pied de page.
        var resx = Lire("src", "DrDxt.Infrastructure", "Resources", fichier);
        var valeur = Regex.Match(resx,
            @"<data name=""UI\.Footer\.Version""[^>]*>\s*<value>([^<]*)</value>").Groups[1].Value;

        valeur.Should().Contain("{0}", "la version vient de l'assembly");
        Regex.IsMatch(valeur, @"\d+\.\d+").Should().BeFalse("un numero ecrit a la main est une seconde source");
    }
}
