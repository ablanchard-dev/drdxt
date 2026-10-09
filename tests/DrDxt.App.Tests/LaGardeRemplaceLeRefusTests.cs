using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// 08/10/2026 (le mainteneur : « DrDXT n'est pas censé me dire non, il est censé trouver, réparer et faire ») : des arrêts récents ne
/// bloquent plus Optimiser. Le texte au joueur dit la garde qui remplace le refus, et où sont la cause et la réparation.
/// </summary>
public sealed class LaGardeRemplaceLeRefusTests
{
    [Theory]
    [InlineData("fr", "sous garde", "Windows et stabilité", "n'écrit rien")]
    [InlineData("en", "under guard", "Windows and stability", "writes nothing")]
    public void Le_texte_dit_la_garde_et_ou_est_la_cause(string culture, string garde, string page, string ancien)
        => new ResxLocalizer().Get("Apply.SousGarde", new CultureInfo(culture))
            .Should().Contain(garde).And.Contain(page).And.NotContain(ancien);
}
