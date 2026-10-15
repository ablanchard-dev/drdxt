using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Quatre dépendances de `PrivilegedOpExecutor` sont optionnelles : absentes, leurs
// ops échouent honnêtement (« not wired »). Le produit ne tient donc que si le helper réel les
// branche. `Program.cs` est un programme de haut niveau, pas instanciable ici : ce test lit la
// construction de l'exécuteur et exige les quatre. Un oubli rendait « Redémarrer vers le BIOS »,
// le manifeste, le plafond réseau ou le BCD muets, sans qu'aucun test ne rougisse.
// -----------------------------------------------------------------------
public class LeHelperBrancheSesQuatrePortesTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln")))
            d = d.Parent;
        d.Should().NotBeNull();
        return d!.FullName;
    }

    [Theory]
    [InlineData("new Win32FirmwareBootActions()")]
    [InlineData("new ManifestCommitActions()")]
    [InlineData("new Win32NetworkQosActions()")]
    [InlineData("new Win32BcdActions()")]
    [InlineData("new Win32PnpActions()")]
    // La cinquieme, le reglage que porte le service de demarrage.
    [InlineData("PlafondEnvoiDuService.ParDefaut()")]
    public void Le_helper_reel_branche_la_porte(string porte)
    {
        var programme = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.ElevatedHelper", "Program.cs"));
        var debut = programme.IndexOf("new PrivilegedOpExecutor(", StringComparison.Ordinal);
        debut.Should().BeGreaterThanOrEqualTo(0, "témoin : la construction de l'exécuteur est trouvée");
        var fin = programme.IndexOf(");", debut, StringComparison.Ordinal);
        // La construction se termine au premier « ); » qui ferme l'appel : les portes sont toutes
        // des « new X() » sans argument, donc aucune ne contient « ); » avant la dernière.
        var construction = programme[debut..(fin + 2)];

        construction.Should().Contain(porte, "absente, l'op correspondante échoue à chaque demande");
    }
}
