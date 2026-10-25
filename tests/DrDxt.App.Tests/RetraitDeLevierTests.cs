using System.IO;
using DrDxt.App.Platform;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Ce qui suit une annulation retire VRAIMENT le levier du manifeste signé : l'identifiant de l'App
// (« latency.hagsOn ») se traduit en celui du service (« os.hags »), et le retrait n'est pas un manifeste privé du
// levier que le magasin FUSIONNERAIT. Sinon le service reposerait au démarrage un levier annulé, et la
// désinstallation en garderait la première origine.
public sealed class RetraitDeLevierTests
{
    private readonly string _journal = Path.Combine(Path.GetTempPath(), "drdxt-retrait-" + Guid.NewGuid().ToString("N") + ".json");

    [Theory]
    [InlineData("latency.hagsOn", "os.hags")]
    [InlineData("latency.vrrFlipModelOn", null)]
    public async Task L_annulation_retire_le_levier_sous_ses_deux_noms(string id, string? auService)
    {
        var manifeste = Substitute.For<IDesiredStateStore>();
        manifeste.RetirerAsync(default!).ReturnsForAnyArgs(Result.Ok());

        await RetraitDeLevier.ApresAnnulationAsync(manifeste, new JournalDesLeviersAppliques(_journal), id);

        var attendus = auService is null ? new[] { id } : new[] { id, auService };
        await manifeste.Received(1).RetirerAsync(Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(attendus)));
        await manifeste.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
    }

    [Fact]
    public async Task Un_retrait_refuse_ou_qui_jette_rend_un_echec()
    {
        var refuse = Substitute.For<IDesiredStateStore>();
        refuse.RetirerAsync(default!).ReturnsForAnyArgs(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));
        var jette = Substitute.For<IDesiredStateStore>();
        jette.RetirerAsync(default!).ReturnsForAnyArgs<Task<Result>>(_ => throw new IOException("verrou"));

        (await RetraitDeLevier.ApresAnnulationAsync(refuse, new JournalDesLeviersAppliques(_journal), "os.hags")).IsOk.Should().BeFalse();
        (await RetraitDeLevier.ApresAnnulationAsync(jette, new JournalDesLeviersAppliques(_journal), "os.hags"))
            .Error!.Value.Message.Should().Contain("verrou");
    }

    // Un journal illisible ne doit pas laisser le levier au manifeste, sinon le service relit une
    // annulation voulue comme une dérive (ou la repose). Le manifeste se nettoie toujours ; l'échec du journal se dit après.
    [Fact]
    public async Task Un_journal_illisible_n_empeche_pas_de_nettoyer_le_manifeste_et_l_echec_se_dit()
    {
        File.WriteAllText(_journal, "null");
        var manifeste = Substitute.For<IDesiredStateStore>();
        manifeste.RetirerAsync(default!).ReturnsForAnyArgs(Result.Ok());

        var r = await RetraitDeLevier.ApresAnnulationAsync(manifeste, new JournalDesLeviersAppliques(_journal), "latency.hagsOn");

        await manifeste.Received(1).RetirerAsync(Arg.Any<IReadOnlyList<string>>());
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("journal");
    }
}
