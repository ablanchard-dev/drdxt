using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// Les phrases du démarrage nommaient le réglage par son identifiant brut (« latency.hagsOn ») : elles le nomment par son
// titre affiché, que l'App leur passe.
public sealed class RevertAuDemarrageNommeLeReglageTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"drdxt-rv-nom-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_fichier); } catch (IOException) { } }

    [Fact]
    public void La_phrase_du_demarrage_nomme_le_reglage_par_son_titre()
    {
        var journal = new FichierRevertEnAttente(_fichier);
        journal.Poser("latency.hagsOn", null).IsOk.Should().BeTrue();

        var phrase = RevertAuDemarrage.Executer(journal, Substitute.For<ITweakCatalog>(), out _,
            id => id == "latency.hagsOn" ? "Planification GPU matérielle" : id);

        phrase.Should().Contain("Planification GPU matérielle").And.NotContain("latency.hagsOn");
    }
    private static ITweak LevierQuiNeSeRendPas()
    {
        var levier = Substitute.For<ITweak>();
        levier.Id.Returns("latency.hagsOn");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("HELPER.OP_FAILED", "Registry write denied by policy"));
        return levier;
    }

    // Le message d'un échec (souvent l'anglais du helper) se collait à la phrase du démarrage : l'App passe la raison
    // lisible ; sans elle, le code seul, jamais le message.
    [Fact]
    public void Un_rendu_rate_dit_la_raison_lisible_jamais_le_message_technique()
    {
        var journal = new FichierRevertEnAttente(_fichier);
        journal.Poser("latency.hagsOn", new TweakSnapshot("latency.hagsOn", DrDxt.Tests.Shared.InversesDeTest.Pour("latency.hagsOn"))).IsOk.Should().BeTrue();
        var catalogue = new DrDxt.Application.Tweaks.TweakCatalog(new[] { LevierQuiNeSeRendPas() });

        var avecRaison = RevertAuDemarrage.Executer(journal, catalogue, out _, raison: _ => "raison lisible");
        var sansRaison = RevertAuDemarrage.Executer(journal, catalogue, out _);

        avecRaison.Should().Contain("raison lisible").And.NotContain("Registry write denied");
        sansRaison.Should().Contain("HELPER.OP_FAILED").And.NotContain("Registry write denied");
    }
}
