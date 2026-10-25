using System.Text;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// Les fichiers des jeux et des lanceurs sont lus avec un plafond : un fichier de plusieurs Go ne vide pas la mémoire.
public sealed class FichierTiersTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), $"tiers-{Guid.NewGuid():N}.txt");

    public void Dispose() => File.Delete(_f);

    [Fact]
    public void Un_fichier_sous_le_plafond_se_lit_comme_ReadAllText()
    {
        File.WriteAllText(_f, "clé=valeur", Encoding.Unicode);

        FichierTiers.Lire(_f).Should().Be("clé=valeur");
    }

    [Fact]
    public void Au_dela_du_plafond_la_lecture_leve_une_erreur_d_entree_sortie()
    {
        File.WriteAllText(_f, new string('x', (int)FichierTiers.OctetsMax + 1));

        FluentActions.Invoking(() => FichierTiers.Lire(_f)).Should().Throw<IOException>();
    }
}
