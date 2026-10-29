using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La protection du ping en partie : la veille, la bascule et son bail.</summary>
public sealed partial class CartesAgirTests
{
    [Fact]
    public async Task Sans_mesure_en_jeu_la_bascule_ping_refuse_en_le_disant_et_n_ecrit_rien()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.PlafondEnvoiIndisponible.Should().BeTrue("aucun bufferbloat mesure hors jeu");

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("Ping.Protection.Echec");
        await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Sans_mesure_la_carte_propose_de_surveiller_les_parties()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
        vm.VeillePingProposee.Should().BeTrue();
        vm.LabelVeilleBascule.Should().Be("Ping.Veille.Libelle");

        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        vm.VeillePingProposee.Should().BeFalse("une mesure existe : la bascule de la protection prend la place");
    }

    [Fact]
    public async Task La_veille_allume_la_protection_sur_un_bufferbloat_vu_en_partie()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
        vm.JeuPourLaVeille = () => JeuAbsent;
        vm.VeillePingActive = true;

        await vm.SurveillerLesPartiesAsync(CancellationToken.None);

        vm.VeillePingActive.Should().BeFalse();
        vm.PlafondEnvoiDisponible.Should().BeTrue("le plafond vient du verdict de la partie");
        vm.PlafondEnvoiActif.Should().BeTrue();
        vm.ErreurPlafondEnvoi.Should().BeNull();
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await vm.DesactiverPlafondEnvoiAsync();
    }

    // Pendant la veille, la commande restait grisée (l'interrupteur ne s'éteignait plus) ; et un clic sans
    // effet (veille plus proposée) laissait l'interrupteur coché par WPF sans que l'état le redise.
    [Fact]
    public async Task L_interrupteur_de_la_veille_reste_cliquable_et_redit_son_etat()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(12));
        vm.JeuPourLaVeille = () => null;
        vm.AttenteVeille = (d, ct) => Task.Delay(Timeout.Infinite, ct);

        var enCours = vm.BasculerVeillePingCommand.ExecuteAsync(null);
        vm.VeillePingActive.Should().BeTrue();
        vm.BasculerVeillePingCommand.CanExecute(null).Should().BeTrue("on doit pouvoir l'éteindre pendant qu'elle tourne");
        await vm.BasculerVeillePingCommand.ExecuteAsync(null);
        await enCours;
        vm.VeillePingActive.Should().BeFalse();

        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        var redit = new List<string?>();
        vm.PropertyChanged += (_, e) => redit.Add(e.PropertyName);
        await vm.BasculerVeillePingCommand.ExecuteAsync(null);
        vm.VeillePingActive.Should().BeFalse();
        redit.Should().Contain(nameof(vm.VeillePingActive), "l'interrupteur coché par le clic doit se recaler");
    }

    [Fact]
    public async Task La_veille_allumee_se_relance_au_lancement_suivant_et_s_oublie_une_fois_arretee()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-veille-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chemin = System.IO.Path.Combine(dossier, "v.json");
            DrDxt.App.ViewModels.ScanViewModel Monter()
            {
                var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
                vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(12));
                vm.JeuPourLaVeille = () => null; // aucune partie : la veille attend
                vm.AttenteVeille = (d, ct) => Task.Delay(Timeout.Infinite, ct);
                vm.MemoireVeille = new DrDxt.Application.Network.VeillePingMemorisee(chemin);
                return vm;
            }

            var premier = Monter();
            _ = premier.BasculerVeillePingCommand.ExecuteAsync(null);
            premier.VeillePingActive.Should().BeTrue();

            var second = Monter();
            second.RallumerVeillePing();
            second.VeillePingActive.Should().BeTrue("le joueur l'avait laissee allumee");

            await second.BasculerVeillePingCommand.ExecuteAsync(null);
            second.VeillePingActive.Should().BeFalse();
            var troisieme = Monter();
            troisieme.RallumerVeillePing();
            troisieme.VeillePingActive.Should().BeFalse("arretee, elle ne revient pas");

            await premier.BasculerVeillePingCommand.ExecuteAsync(null);
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task Un_plafond_trouve_eteint_la_veille_memorisee()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-veille-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.VeillePingMemorisee(System.IO.Path.Combine(dossier, "v.json"));
            memoire.Allumer();
            var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
            vm.JeuPourLaVeille = () => JeuAbsent;
            vm.MemoireVeille = memoire;

            await vm.SurveillerLesPartiesAsync(CancellationToken.None);

            memoire.EstAllumee().Should().BeFalse("la memoire du plafond prend le relais");
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task Un_envoi_sain_ne_pose_rien_et_l_arret_ne_laisse_rien()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        using var arret = new CancellationTokenSource();
        var fenetres = 0;
        vm.CollecteurFenetre = (_, _) =>
        {
            if (++fenetres == 3) arret.Cancel();
            return Task.FromResult(Fenetre(12));
        };
        vm.JeuPourLaVeille = () => JeuAbsent;

        await vm.SurveillerLesPartiesAsync(arret.Token);

        vm.PlafondEnvoiDisponible.Should().BeFalse();
        vm.PlafondEnvoiActif.Should().BeFalse();
        await helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Apres_une_mesure_en_jeu_la_bascule_ouvre_le_bail_puis_retire_tout_en_s_eteignant()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        vm.PlafondEnvoiDisponible.Should().BeTrue();
        // Le plafond est dit avant le clic (clé de ressource : le localiseur de test rend la clé).
        vm.TextePlafondEnvoi.Should().Contain("Ping.Protection.Plafond");

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeTrue();
        vm.LabelPingEtat.Should().Be("Ping.Protection.Active");
        vm.ErreurPlafondEnvoi.Should().BeNull();
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.LabelPingEtat.Should().Be("Ping.Protection.Inactive");
        await helper.Received().ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is RemoveUploadCapOp), Arg.Any<CancellationToken>());
    }

    // Éteinte, la protection ne se dit « inactive » sans réserve que si le helper a relu la politique partie. Refusé, le
    // retrait se dit, et ce qui le fera quand même : le bail du helper, puis le redémarrage.
    [Fact]
    public async Task Un_retrait_refuse_en_eteignant_se_dit()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeTrue();
        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is RemoveUploadCapOp), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "non")));

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.ErreurPlafondEnvoi.Should().StartWith("Ping.Protection.RetraitDiffere");
    }
}
