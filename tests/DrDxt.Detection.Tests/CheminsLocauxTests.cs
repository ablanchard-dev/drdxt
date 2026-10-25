using FluentAssertions;

namespace DrDxt.Detection.Tests;

// Un chemin lu dans une valeur que tout programme du compte peut écrire (registre de l'utilisateur, tâche, lanceur) ne se
// suit que s'il est local : une lettre de lecteur. Un partage réseau ouvrirait une connexion SMB, donc une authentification
// du compte vers l'hôte nommé.
public sealed class CheminsLocauxTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Steam", true)]
    [InlineData(@"d:/Jeux", true)]
    [InlineData(@"\hote\partage\Steam", false)]
    [InlineData(@"\\hote\partage\Steam", false)]
    [InlineData(@"//hote/partage", false)]
    [InlineData(@"\?\UNC\hote\partage", false)]
    [InlineData(@"\\?\UNC\hote\partage", false)]
    [InlineData(@"\?\C:\Jeux", false)]
    [InlineData(@"\\?\C:\Jeux", false)]
    [InlineData(@"Steam", false)]
    [InlineData(@"C:Jeux", false)]
    [InlineData("", false)]
    public void Seul_un_chemin_sous_une_lettre_de_lecteur_est_local(string chemin, bool local)
        => CheminsLocaux.EstLocal(chemin).Should().Be(local);
}
