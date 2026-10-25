using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Octets du build livré lus un par un : la DLL portait
// « C:\Users\alicejoueuse\DrDXT\src\...\DrDxt.App.pdb », et les PDB livrés à côté mettaient les chemins
// sources de la machine du développeur dans chaque pile de plantage (crash.log). Un utilisateur qui
// envoie son crash.log envoyait le nom du compte Windows.
//
// La règle : en Release, la compilation est déterministe et remplace la racine locale par « /_/ »
// (ContinuousIntegrationBuild). La preuve réelle est le scan des octets publiés ; ce test garde
// seulement que la propriété ne disparaisse pas.
// -----------------------------------------------------------------------
public sealed class LaCompilationLivreeNePortePasLeCheminDuDeveloppeurTests
{
    [Fact]
    public void Le_release_remplace_les_chemins_locaux()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var props = XDocument.Load(Path.Combine(d!.FullName, "Directory.Build.props"));

        props.Descendants("ContinuousIntegrationBuild")
            .Should().Contain(e => e.Value == "true"
                && ((string?)e.Parent!.Attribute("Condition") ?? string.Empty).Contains("Release"),
                "sans elle, chaque pile de plantage d'un utilisateur porte C:\\Users\\<développeur>");
    }

    // Le lanceur est compilé en natif : son éditeur de liens écrit le chemin du .pdb, que la propriété ci-dessus ne touche
    // pas. La construction de l'installeur scanne les octets de chaque binaire livré, et vérifie PresentMon.
    [Fact]
    public void Le_lanceur_natif_et_la_construction_de_l_installeur_gardent_les_binaires_propres()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;

        XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.Lanceur", "DrDxt.Lanceur.csproj"))
            .Descendants("LinkerArg").Should().Contain(e => (string?)e.Attribute("Include") == "/PDBALTPATH:%_PDB%");
        File.ReadAllText(Path.Combine(d.FullName, "installer", "build-installer.ps1")).Should()
            .Contain("throw \"Chemin du developpeur dans")
            .And.Contain("throw \"Empreinte differente de VERSION.txt")
            .And.Contain("O=Intel Corporation");
    }
}
