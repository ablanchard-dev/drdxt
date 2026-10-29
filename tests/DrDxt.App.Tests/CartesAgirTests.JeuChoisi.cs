using System.IO;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La protection du ping et le jeu choisi sous Optimiser : choisi, il passe avant le jeu où la mesure l'a vue ;
/// automatique, rien ne change.</summary>
public sealed partial class CartesAgirTests
{
    [Theory]
    [InlineData("JeuA.exe", "JeuA.exe")]
    [InlineData(null, "JeuB.exe")]
    public async Task La_protection_vise_le_jeu_choisi_sinon_celui_de_la_mesure(string? choisi, string vise)
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-ping-jeu-").FullName;
        try
        {
            var helper = Helper(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.JeuPorteParLeService = _ => true;
            vm.MemoireJeuChoisi = new MemoireDuJeuChoisi(Path.Combine(dossier, "jeu-choisi.json"));
            vm.MemoireJeuChoisi.Choisir(choisi is null ? null : new JeuChoisi(choisi));
            vm.RetenirMesureEnvoi(20_000_000, "JeuB.exe");

            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

            await helper.Received(1).ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp && ((RetainServiceUploadCapOp)o).GameExeName == vise),
                Arg.Any<CancellationToken>());
            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        }
        finally { Directory.Delete(dossier, true); }
    }

    // La protection active suit le choix : rallumée sur le nouveau jeu, l'ancien oublié. Jamais deux jeux plafonnés.
    [Fact]
    public async Task Changer_de_jeu_recible_la_protection_active()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-ping-jeu-").FullName;
        try
        {
            var helper = Helper(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.JeuPorteParLeService = _ => true;
            vm.MemoireJeuChoisi = new MemoireDuJeuChoisi(Path.Combine(dossier, "jeu-choisi.json"));
            vm.RetenirMesureEnvoi(20_000_000, "JeuB.exe");
            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            vm.PlafondEnvoiActif.Should().BeTrue("témoin : active sur JeuB");

            await vm.ChoisirJeuCommand.ExecuteAsync("JeuA.exe");

            vm.PlafondEnvoiActif.Should().BeTrue();
            await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
            await helper.Received(1).ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp && ((RetainServiceUploadCapOp)o).GameExeName == "JeuA.exe"),
                Arg.Any<CancellationToken>());

            await vm.ChoisirJeuCommand.ExecuteAsync("JeuA.exe");
            await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        }
        finally { Directory.Delete(dossier, true); }
    }
}
