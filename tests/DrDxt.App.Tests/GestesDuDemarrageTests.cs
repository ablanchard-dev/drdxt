using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Les programmes au démarrage : un geste « couper » par programme actif, son Annuler le réactive. La sécurité de Windows
// et les programmes déjà coupés n'en ont pas.
public sealed class GestesDuDemarrageTests
{
    private static readonly ProgrammesAuDemarrage.Entree Discord = new("Discord", true, "discord.exe");
    private static readonly ProgrammesAuDemarrage.Entree Securite = new("SecurityHealth", true, "x", Ruche: RegistryHiveKind.LocalMachine);
    private static readonly ProgrammesAuDemarrage.Entree DejaCoupe = new("Steam", false, "steam.exe");

    [Fact]
    public async Task Un_geste_par_programme_actif_et_son_annuler_le_reactive()
    {
        var envoyes = new List<PrivilegedOp>();
        var gestes = ScanViewModel.GestesDuDemarrage([Discord, Securite, DejaCoupe],
            op => { envoyes.Add(op); return Task.FromResult(Result.Ok()); }, cle => cle + "|{0}").Select(g => g.Creer()).ToList();

        gestes.Should().ContainSingle().Which.Titre.Should().Contain("Discord");
        await gestes[0].FaireCommand.ExecuteAsync(null);
        await gestes[0].AnnulerCommand.ExecuteAsync(null);
        envoyes.Should().Equal(Discord.Geste(false), Discord.Geste(true));
    }

    [Fact]
    public void Rien_d_actif_rien_a_proposer()
        => ScanViewModel.GestesDuDemarrage([DejaCoupe], _ => Task.FromResult(Result.Ok()), c => c).Should().BeEmpty();
}
