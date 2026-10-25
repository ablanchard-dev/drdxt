using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Chaque processus élevé de DrDXT pose RedirectionGuard au démarrage, avant toute écriture : le helper (et la désinstallation,
// qu'il porte), le service SYSTEM, l'App relancée en administrateur. Un nouveau point d'entrée élevé qui l'oublierait suivrait
// les jonctions d'un compte sans droits.
public sealed class ChaqueProcessusEleveRefuseLesJonctionsDuCompteTests
{
    private static string Lire(params string[] morceaux)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine([d!.FullName, .. morceaux]));
    }

    [Theory]
    [InlineData("src", "DrDxt.ElevatedHelper", "Program.cs", "private static async Task<int> Main(")]
    [InlineData("src", "DrDxt.ReconcilerHost", "Program.cs", "var builder = Host.CreateApplicationBuilder(args);")]
    [InlineData("src", "DrDxt.App", "App.xaml.cs", "protected override void OnStartup(")]
    public void Le_point_d_entree_pose_RedirectionGuard_avant_le_reste(string a, string b, string c, string temoin)
    {
        var source = Lire(a, b, c);
        var debut = source.IndexOf(temoin, StringComparison.Ordinal);
        debut.Should().BeGreaterThanOrEqualTo(0, "témoin : le point d'entrée est trouvé");

        var activation = source.IndexOf("ConfianceDesRedirections.Activer()", StringComparison.Ordinal);
        activation.Should().BeGreaterThanOrEqualTo(0);
        // Le service l'active avant même de construire l'hôte ; les deux autres au début de leur méthode d'entrée.
        if (c == "Program.cs" && b == "DrDxt.ReconcilerHost") activation.Should().BeLessThan(debut);
        else activation.Should().BeGreaterThan(debut).And.BeLessThan(source.IndexOf("ServiceCollection", debut) is var s and > 0 ? s : int.MaxValue);
    }
}
