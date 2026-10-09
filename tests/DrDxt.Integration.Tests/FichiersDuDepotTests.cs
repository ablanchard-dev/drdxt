using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 23/09/2026 — le rouge intermittent de <c>AucunOctetNulDansLesSourcesTests</c> (vu trois fois),
/// enfin nommé : <c>FileNotFoundException</c> sur <c>src\DrDxt.App\DrDxt.App_lahfo0ht_wpftmp.csproj</c>.
/// Pendant la compilation XAML, WPF écrit un projet temporaire À côté du vrai, pas dans
/// <c>obj</c>, puis l'efface. Une garde qui liste le dossier pendant ce temps le voit, puis ne
/// peut plus le lire. Ce n'est pas une source : le parcours du dépôt ne le rend jamais.
/// </summary>
public sealed class FichiersDuDepotTests
{
    [Fact]
    public void Le_projet_temporaire_de_la_compilation_WPF_n_est_pas_une_source()
    {
        var racine = Directory.CreateTempSubdirectory("drdxt-depot-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(racine, "DrDxt.App.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(racine, "DrDxt.App_lahfo0ht_wpftmp.csproj"), "<Project/>");

            var noms = FichiersDuDepot.Sous(racine).Select(Path.GetFileName).ToList();

            noms.Should().Contain("DrDxt.App.csproj", "témoin : le vrai projet reste lu");
            noms.Should().NotContain("DrDxt.App_lahfo0ht_wpftmp.csproj");
        }
        finally
        {
            Directory.Delete(racine, recursive: true);
        }
    }
}
