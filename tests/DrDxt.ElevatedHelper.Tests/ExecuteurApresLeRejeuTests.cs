using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// Le rejeu rend l'origine laissée par une App morte ; l'App relancée qui pose le même réglage au même instant notait la
// valeur de DrDXT comme image d'avant. Ce que le tube fait exécuter attend la fin du rejeu.
public sealed class ExecuteurApresLeRejeuTests
{
    private static readonly PrivilegedOp Op = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Test", "V", RegistryValueType.DWord, "1");

    [Fact]
    public async Task Une_ecriture_du_tube_attend_la_fin_du_rejeu_en_cours()
    {
        var fin = new TaskCompletionSource<RapportDuRendu?>();
        var rejeuEnCours = new TaskCompletionSource();
        var rejeu = new RejeuEnSerie(_ => { rejeuEnCours.TrySetResult(); return fin.Task; }, _ => { }, TimeSpan.FromSeconds(30));
        var interieur = Substitute.For<IPrivilegedOpExecutor>();
        interieur.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        var servi = new ExecuteurApresLeRejeu(interieur, rejeu);

        var rendu = rejeu.Demander();
        await rejeuEnCours.Task;
        var ecriture = servi.ExecuteAsync(Op, CancellationToken.None);
        await Task.Delay(200);

        await interieur.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        fin.SetResult(null);
        await rendu;
        (await ecriture).IsOk.Should().BeTrue();
        await interieur.Received(1).ExecuteAsync(Op, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sans_rejeu_l_ecriture_part_aussitot()
    {
        var rejeu = new RejeuEnSerie(_ => Task.FromResult<RapportDuRendu?>(null), _ => { }, TimeSpan.FromSeconds(30));
        var interieur = Substitute.For<IPrivilegedOpExecutor>();
        interieur.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());

        (await new ExecuteurApresLeRejeu(interieur, rejeu).ExecuteAsync(Op, CancellationToken.None)).IsOk.Should().BeTrue();
    }
}
