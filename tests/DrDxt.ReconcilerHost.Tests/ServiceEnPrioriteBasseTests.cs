using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>
/// Le service tourne pendant les parties : il passe après le jeu. Le point d'entrée se lit ici comme du texte, comme le
/// relevé de fond dans les tests de l'App ; l'hôte lancé pour de vrai serait un service Windows.
/// </summary>
public sealed class ServiceEnPrioriteBasseTests
{
    [Fact]
    public void Le_service_se_met_en_priorite_basse_avant_de_construire_l_hote()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var programme = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ReconcilerHost", "Program.cs"));

        var priorite = programme.IndexOf("PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal", StringComparison.Ordinal);
        priorite.Should().BeGreaterThan(0);
        priorite.Should().BeLessThan(programme.IndexOf("Host.CreateApplicationBuilder(args)", StringComparison.Ordinal));
    }
}
