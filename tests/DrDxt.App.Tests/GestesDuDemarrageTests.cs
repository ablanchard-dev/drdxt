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

    // Ce que DrDXT a écrit pour couper Steam, horodatage compris : la trace le garde, la machine le porte encore.
    private const string OctetsCoupe = "030000000102030405060708";
    private static readonly ProgrammesAuDemarrage.Entree SteamCoupe = new("Steam", false, "steam.exe", Octets: OctetsCoupe);
    private static readonly DrDxt.ElevatedHelper.Execution.CoupureDuDemarrage Trace = new(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "Steam", OctetsCoupe);

    private static List<GesteDuDocteur> Gestes(IReadOnlyList<ProgrammesAuDemarrage.Entree> entrees, List<PrivilegedOp> envoyes,
        params DrDxt.ElevatedHelper.Execution.CoupureDuDemarrage[] coupures)
        => ScanViewModel.GestesDuDemarrage(entrees, op => { envoyes.Add(op); return Task.FromResult(Result.Ok()); }, c => c + "|{0}", coupures)
            .Select(g => g.Creer()).ToList();

    // L'App tuée après avoir coupé Steam : relancée, elle sait encore le réactiver.
    [Fact]
    public async Task Un_programme_coupe_par_DrDXT_se_reactive_apres_un_redemarrage_de_l_App()
    {
        var envoyes = new List<PrivilegedOp>();

        var gestes = Gestes([SteamCoupe], envoyes, Trace);

        gestes.Should().ContainSingle().Which.PeutAnnuler.Should().BeTrue("la coupure est déjà faite, son annulation proposée");
        await gestes[0].AnnulerCommand.ExecuteAsync(null);
        envoyes.Should().Equal(SteamCoupe.Geste(true));
    }

    // Coupé puis réactivé ou recoupé ailleurs, la valeur n'est plus celle que DrDXT a écrite : rien ne se propose.
    [Fact]
    public void Un_programme_modifie_ailleurs_ne_se_propose_pas()
        => Gestes([SteamCoupe with { Octets = "03000000AAAAAAAAAAAAAAAA" }], [], Trace).Should().BeEmpty();

    [Fact]
    public void Un_programme_protege_ne_se_propose_jamais_a_la_reactivation()
    {
        var securite = new ProgrammesAuDemarrage.Entree("SecurityHealth", false, "x", Octets: OctetsCoupe);

        Gestes([securite], [], Trace with { Nom = "SecurityHealth" }).Should().BeEmpty();
    }
}
