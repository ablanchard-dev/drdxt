using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

public class DossierDonneesTests
{
    private static readonly string Normal = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrDXT");

    [Fact]
    public void Sans_variable_les_donnees_sont_sous_LocalAppData()
        => DossierDonnees.Racine(_ => null).Should().Be(Normal);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relatif\\dossier")]
    [InlineData("C:dossier")]
    public void Une_valeur_vide_ou_relative_ne_redirige_rien(string valeur)
        => DossierDonnees.Racine(_ => valeur).Should().Be(Normal);

#if DEBUG
    [Fact]
    public void En_developpement_un_chemin_absolu_redirige_les_donnees()
    {
        var jetable = Path.Combine(Path.GetTempPath(), "drdxt-photo");
        DossierDonnees.Racine(n => n == DossierDonnees.VariableDeDeveloppement ? jetable : null).Should().Be(jetable);
    }
#else
    [Fact]
    public void Une_version_publiee_ignore_la_variable()
        => DossierDonnees.Racine(_ => Path.GetTempPath()).Should().Be(Normal);
#endif
}
