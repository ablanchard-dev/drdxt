using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le programme élevé reconnaît l'appli à son fichier. Un programme du même compte,
// sans droits, pouvait lancer le vrai DrDxt.App.exe avec DOTNET_STARTUP_HOOKS et y faire tourner son code, qui parlait
// alors au programme élevé comme l'appli. Les crochets de démarrage sont coupés dans les deux exécutables.
public sealed class AucunCrochetDeDemarrageTests
{
    [Theory]
    [InlineData("DrDxt.App.runtimeconfig.json")]
    [InlineData("DrDxt.ElevatedHelper.runtimeconfig.json")]
    public void Les_crochets_de_demarrage_sont_coupes(string config)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, config))
            .Should().Contain("\"System.StartupHookProvider.IsSupported\": false");
}
