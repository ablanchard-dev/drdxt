using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La protection portée par le service de démarrage, et le plafond qui suit le ping mesuré.</summary>
public sealed partial class CartesAgirTests
{
    // La protection retenue part aussi au service de démarrage, par le helper (seul à écrire sous
    // %ProgramData%\DrDxt). Éteinte, elle y est oubliée : le service ne plafonne plus rien.
    [Fact]
    public async Task La_bascule_retient_la_protection_pour_le_service_puis_l_oublie()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp
                                      && ((RetainServiceUploadCapOp)o).GameExeName == JeuAbsent
                                      && ((RetainServiceUploadCapOp)o).CapBitsPerSecond == 20_000_000),
            Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
    }

    // Un seul poseur. Service de démarrage en marche : il porte la protection, l'App n'arme pas son
    // propre mode (deux poseurs se retireraient l'un l'autre le plafond quand l'App se ferme en pleine partie).
    [Fact]
    public async Task Service_en_marche_l_App_n_arme_pas_son_propre_mode_mais_la_protection_est_active()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeTrue();
        await helper.DidNotReceive().ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp), Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
    }

    // Service en marche, l'App ne pose rien elle-même ; si le helper refuse de retenir la protection
    // pour le service (UAC déclinée), personne ne la porte : la carte ne doit pas dire « active ».
    [Fact]
    public async Task Service_en_marche_un_refus_de_retenir_laisse_la_protection_eteinte_et_le_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse")));
        vm.JeuPorteParLeService = _ => true;
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse("personne ne porte la protection");
        vm.ErreurPlafondEnvoi.Should().NotBeNull();
    }

    // Un jeu que le service ne porte pas (absent de la liste des jeux reconnus) : le service en marche n'éteint pas sa
    // protection, l'App la porte elle-même avec son bail, et rien n'est retenu pour le service.
    [Fact]
    public async Task Service_en_marche_un_jeu_non_reconnu_garde_la_protection_de_l_App()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeTrue();
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await helper.DidNotReceive().ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp), Arg.Any<CancellationToken>());
    }

    // Symétrique du refus à l'allumage. Éteindre demande au helper d'oublier la protection sous
    // %ProgramData% ; refusé, le service continue de plafonner à chaque partie : la carte ne doit pas dire « inactive ».
    [Fact]
    public async Task Service_en_marche_un_refus_d_oublier_laisse_la_protection_active_et_le_dit()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeTrue("témoin : allumée et retenue pour le service");

        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse")));
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeTrue("le service la porte toujours");
        vm.ErreurPlafondEnvoi.Should().NotBeNull();
    }

    // « Elle n'agit que pendant que DrDXT est ouvert » est faux dès que le service de
    // démarrage la porte : le texte suit celui qui pose le plafond.
    [Fact]
    public void La_carte_dit_qui_porte_la_protection()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));

        vm.LabelPingDescription.Should().Be("Ping.Protection.Description");

        vm.ServiceDemarrageActif = () => true;
        vm.LabelPingDescription.Should().Be("Ping.Protection.Description.Service");
    }

    // La fenêtre s'affiche avant que l'App branche la question du service : sans prévenir la carte,
    // elle garderait le texte lu à l'affichage. Brancher la question doit prévenir la carte.
    [Fact]
    public void Brancher_la_question_du_service_previent_la_carte()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        var prevenus = new List<string?>();
        vm.PropertyChanged += (_, e) => prevenus.Add(e.PropertyName);

        vm.ServiceDemarrageActif = () => true;

        prevenus.Should().Contain(nameof(vm.LabelPingDescription));
    }

    [Fact]
    public void La_carte_nomme_le_jeu_que_la_protection_surveille()
    {
        // « Le jeu, lui, n'est pas plafonne » nomme le jeu : on sait
        // quel jeu est protege ; et un fichier retenu modifie (explorer.exe) se voit tout de suite.
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()),
            loc: k => k == "Ping.Protection.Plafond" ? "{0:0.#} Mbit/s, jeu : {1}" : k);
        vm.RetenirMesureEnvoi(20_000_000, "cod.exe");
        vm.TextePlafondEnvoi.Should().Contain("cod.exe");
    }

    [Fact]
    public async Task Une_seconde_activation_pendant_l_attente_de_la_premiere_n_ouvre_pas_un_second_mode()
    {
        // Au lancement, le rallumage attend le helper (UAC) ; un clic pendant ce temps
        // passait le garde (le mode n'est retenu qu'apres l'attente). Deux modes, deux boucles : en
        // eteignant, seule la seconde s'arretait, et la premiere reposait le plafond au jeu suivant.
        var bail = new TaskCompletionSource<Result>();
        var helper = Substitute.For<IElevatedClient>();
        helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>()).Returns(bail.Task);
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        var premiere = vm.ActiverPlafondEnvoiAsync();
        var seconde = vm.ActiverPlafondEnvoiAsync();
        bail.SetResult(Result.Ok());
        await Task.WhenAll(premiere, seconde);

        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await vm.DesactiverPlafondEnvoiAsync();
    }

    // Une protection allumée avant le service n'est retenue que sous %LOCALAPPDATA%. Sans transmission, au premier
    // lancement avec le service, la carte dirait « le service la porte » alors qu'il n'a rien à porter. Elle lui
    // est transmise une fois (une invite, comme un rallumage) ; déjà retenue, le lancement ne demande rien.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Service_en_marche_le_rallumage_transmet_au_service_ce_qu_il_ne_porte_pas_encore(bool dejaRetenu, int ordres)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            memoire.Retenir(new(JeuAbsent, 20_000_000, DateTimeOffset.Now));
            var helper = Helper(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
            vm.MemoirePlafond = memoire;
            vm.ServiceDemarrageActif = () => true;
            vm.ServicePorteUnRetenu = () => dejaRetenu;

            await vm.RallumerPlafondEnvoiAsync();

            vm.PlafondEnvoiActif.Should().BeTrue();
            helper.ReceivedCalls().Count(c => c.GetArguments()[0] is RetainServiceUploadCapOp).Should().Be(ordres);
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task La_protection_allumee_se_rallume_au_lancement_suivant_et_s_oublie_une_fois_eteinte()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            vm.MemoirePlafond = memoire;
            vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            memoire.Lire()!.Exe.Should().Be(JeuAbsent);

            // Un nouveau lancement : un autre ViewModel, aucun scan, le meme fichier.
            var relance = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            relance.MemoirePlafond = memoire;
            await relance.RallumerPlafondEnvoiAsync();
            relance.PlafondEnvoiActif.Should().BeTrue("le joueur l'avait laissee allumee");

            await relance.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            memoire.Lire().Should().BeNull("eteinte, elle ne revient plus");
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    // Le rallumage part au lancement sans être attendu : une exception y disparaîtrait sans un mot,
    // et le joueur croirait sa protection revenue.
    [Fact]
    public async Task Un_rallumage_qui_leve_le_dit_au_lieu_de_se_taire()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-leve-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            memoire.Retenir(new(JeuAbsent, 20_000_000, DateTimeOffset.Now));
            var helper = Substitute.For<IElevatedClient>();
            helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
                .Returns<Task<Result>>(_ => throw new InvalidOperationException("panne imprévue"));
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.MemoirePlafond = memoire;

            await vm.Invoking(v => v.RallumerPlafondEnvoiAsync()).Should().NotThrowAsync();
            vm.ErreurPlafondEnvoi.Should().NotBeNullOrEmpty();
            vm.PlafondEnvoiActif.Should().BeFalse();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task En_partie_le_plafond_suit_le_ping_mesure()
    {
        // Le « jeu » est le processus de test lui-meme : il tourne, donc le plafond se pose.
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        // RenewLease est une methode par defaut de l'interface : le substitut ne la route pas vers
        // ExecuteAsync et rendrait null.
        helper.RenewLease(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        // Le tour de 5 s de la boucle, court ici : l'attente du test ne dépend plus de la charge du runner.
        vm.TourDuPlafond = TimeSpan.FromMilliseconds(50);
        // Le jeu est vivant par la couture, pas par une vraie recherche de processus (sinon le test dépend de la machine de CI).
        vm.JeuVivant = exe => exe == jeu;
        // Le mode démarre mais aucun battement ne tourne en
        // 34 s. Hypothèse, non prouvée : la boucle part par Task.Run sur le pool de .NET, que d'autres tests bloquent en
        // parallèle sur le runner, alors que xUnit déroule ce test sur ses propres fils. Un minimum de fils plus haut
        // retire cette famine ; si le rouge revient, l'hypothèse est fausse.
        System.Threading.ThreadPool.GetMinThreads(out var fils, out var es);
        System.Threading.ThreadPool.SetMinThreads(Math.Max(fils, 64), es);
        // Fenetre deterministe : repos 8 ms, envoi au plafond et ping +22 ms => le plafond doit descendre.
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        var journal = Substitute.For<IDiagnosticsLog>();
        vm.JournalPlafond = journal;
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            // La boucle attend 5 s tant que le plafond n'est pas pose, puis une fenetre : deux tours sur un runner charge depassent 10 s.
            var limite = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limite && !helper.ReceivedCalls().Any(c =>
                       c.GetArguments().FirstOrDefault() is ApplyUploadCapOp { CapBitsPerSecond: 16_000_000 }))
                await Task.Delay(50);
            await helper.Received().ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp && ((ApplyUploadCapOp)o).CapBitsPerSecond == 16_000_000),
                Arg.Any<CancellationToken>());
            // La trace de l'essai reel : le changement et les deux p90 qui l'ont decide.
            limite = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limite && !journal.ReceivedCalls().Any()) await Task.Delay(50);
            journal.Received().Change(ScanViewModel.IdJournalPlafond, "20 Mbit/s",
                "16 Mbit/s (ping p90 repos 8 ms, en charge 30 ms)", "DrDXT");
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    // Le helper meurt en pleine partie : le battement ne le relance pas (aucune invite sans geste), le bail ne se renouvelle ni
    // ne se rouvre, rien n'est posé, et la carte dit pourquoi. Le helper revenu, le texte part.
    [Fact]
    public async Task Sans_filet_le_plafond_ne_se_pose_pas_et_la_carte_le_dit()
    {
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Substitute.For<IElevatedClient>();
        helper.SansRelance.Returns(helper);
        var joignable = true;
        helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(joignable ? Result.Ok() : Result.Fail(ErrorCodes.HelperUnreachable, "tube absent")));
        helper.RenewLease(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(joignable ? Result.Ok() : Result.Fail(ErrorCodes.HelperUnreachable, "tube absent")));
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.TourDuPlafond = TimeSpan.FromMilliseconds(50);
        vm.JeuVivant = _ => false;
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            vm.PlafondEnvoiActif.Should().BeTrue();
            joignable = false;
            var limite = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limite && vm.ErreurPlafondEnvoi != "Ping.Protection.SansFilet") await Task.Delay(50);
            vm.ErreurPlafondEnvoi.Should().Be("Ping.Protection.SansFilet");

            joignable = true;
            limite = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limite && vm.ErreurPlafondEnvoi is not null) await Task.Delay(50);
            vm.ErreurPlafondEnvoi.Should().BeNull("un battement a rejoint le helper : le filet est revenu");
        }
        finally
        {
            joignable = true;
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    // Service en marche, l'App n'arme pas son mode et l'ajustement ne tournait plus : le plafond du
    // service restait figé à la valeur du scan. DrDXT ouvert, le plafond suit encore le ping, par le service.
    [Fact]
    public async Task Service_en_marche_le_plafond_suit_encore_le_ping_mesure()
    {
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
        vm.ServiceDemarrageActif = () => true;
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        vm.HelperSansLancer = helper;
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < limite && !helper.ReceivedCalls().Any(c =>
                       c.GetArguments()[0] is RetainServiceUploadCapOp { CapBitsPerSecond: 16_000_000 }))
                await Task.Delay(50);
            await helper.Received().ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp && ((RetainServiceUploadCapOp)o).CapBitsPerSecond == 16_000_000),
                Arg.Any<CancellationToken>());
            await helper.DidNotReceive().ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp), Arg.Any<CancellationToken>());
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    // L'ajustement en mode service passait par le helper qui se lance à la demande, donc une invite
    // UAC en pleine partie, répétée toutes les 20 s si elle était refusée. Il ne passe plus que par un client qui
    // ne lance jamais le helper ; sans lui, le plafond du service reste celui retenu.
    [Fact]
    public async Task Service_en_marche_l_ajustement_ne_lance_jamais_le_helper()
    {
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.JeuPorteParLeService = _ => true;
        vm.ServiceDemarrageActif = () => true;
        vm.FenetreAjustement = TimeSpan.Zero;
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            await Task.Delay(1500);
            helper.ReceivedCalls().Count(c => c.GetArguments()[0] is RetainServiceUploadCapOp)
                .Should().Be(1, "seule la bascule, un geste du joueur, passe par le helper qui peut se lancer");
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    [Fact]
    public async Task Un_bail_refuse_laisse_la_protection_eteinte_et_le_dit()
    {
        var helper = Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse"));
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("Erreur.Generique").And.NotContain("UAC refuse");
        await helper.DidNotReceive().ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp), Arg.Any<CancellationToken>());
    }
}
