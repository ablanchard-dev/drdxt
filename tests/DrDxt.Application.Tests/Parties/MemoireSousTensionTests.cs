using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// La recherche de sous-tension relue au démarrage : un fichier valide mais incohérent n'est pas une recherche.
public sealed class MemoireSousTensionTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"sous-tension-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch { } }

    // Un palier hors de la table, ou des paliers stables absents, faisaient lever la reprise au démarrage.
    [Theory]
    [InlineData("{\"Index\":7,\"Stables\":[],\"Finie\":false}")]
    [InlineData("{\"Index\":-1,\"Stables\":[],\"Finie\":false}")]
    [InlineData("{\"Index\":0,\"Finie\":false}")]
    public void Une_recherche_incoherente_se_relit_comme_aucune(string json)
    {
        File.WriteAllText(_chemin, json);

        new MemoireSousTension(_chemin).Lire().Should().BeNull();
    }

    [Fact]
    public void Une_recherche_coherente_se_relit()
    {
        File.WriteAllText(_chemin, "{\"Index\":1,\"Stables\":[],\"Finie\":false}");

        new MemoireSousTension(_chemin).Lire()!.Index.Should().Be(1);
    }
}
