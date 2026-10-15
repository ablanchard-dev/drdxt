using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Sans cette ligne, le docteur restait en français sur un Windows anglais (sa langue par défaut).
public sealed class LeDocteurParleLaLangueDeWindowsTests
{
    [Fact]
    public void Le_demarrage_de_l_App_fixe_la_langue_du_docteur()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml.cs"));

        var demarrage = app[app.IndexOf("protected override void OnStartup", StringComparison.Ordinal)..];
        demarrage[..Math.Min(1500, demarrage.Length)].Should().Contain("Docteur.FixerLangue(CultureInfo.CurrentUICulture);",
            "fixée au tout début, avant tout constat");
    }
}
