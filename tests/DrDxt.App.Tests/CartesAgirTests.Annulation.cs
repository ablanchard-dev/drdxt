using DrDxt.Tests.Shared;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Annuler depuis l'écran : ce qui est dit fait, refusé ou introuvable, et la remise de la carte à l'usine.</summary>
public sealed partial class CartesAgirTests
{
    // Un réglage posé par DrDXT il y a des jours s'annule depuis l'écran, et pas
    // seulement juste après un réglage de carte graphique.
    [Fact]
    public async Task Un_reglage_pose_par_DrDXT_s_annule_depuis_l_ecran()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", InversesDeTest.Pour("latency.vrrFlipModelOn", "origine"), "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.vrrFlipModelOn" ? levier : null;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };

            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().ContainSingle().Which.Id.Should().Be("latency.vrrFlipModelOn");

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            await levier.Received(1).RevertAsync(pose);
            sortis.Should().Equal("latency.vrrFlipModelOn");
            leviers.Lire().Should().BeEmpty("annulé, il n'est plus « posé par DrDXT »");
            vm.LeviersPoses.Should().BeEmpty();
            vm.MessageLeviers.Should().Be("Leviers.Annule");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Une annulation qui attend (l'invite d'élévation, le retour) est une opération : Optimiser ne pose rien pendant qu'elle
    // retire, et revient après.
    [Fact]
    public async Task Pendant_une_annulation_Optimiser_attend()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", InversesDeTest.Pour("latency.vrrFlipModelOn", "origine"), "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var retour = new TaskCompletionSource<Result>();
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(retour.Task);
            vm.LevierParId = id => id == "latency.vrrFlipModelOn" ? levier : null;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ChargerLeviersPoses();

            var annulation = vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            vm.OperationLongueEnCours.Should().BeTrue();
            vm.ApplyCommand.CanExecute(null).Should().BeFalse();
            retour.SetResult(Result.Ok());
            await annulation;
            vm.OperationLongueEnCours.Should().BeFalse();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // « Annulé » ne se dit que relu : une annulation acceptée dont la machine porte encore la valeur de DrDXT ne retire rien
    // (journal, manifeste, liste) ; illisible ou ni l'une ni l'autre, elle part du journal mais le dit.
    [Theory]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.NotApplied, "Leviers.Annule", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Applied, "Leviers.PasAnnule", false)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Drifted, "Leviers.AnnuleNonRelu", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unknown, "Leviers.AnnuleNonRelu", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unavailable, "Leviers.AnnuleNonRelu", true)]
    public async Task Une_annulation_n_est_dite_faite_que_relue_a_l_origine(DrDxt.Application.Tweaks.EtatApplique relu, string dit, bool retire)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-relu-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", InversesDeTest.Pour("latency.vrrFlipModelOn", "origine"), "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            var rendu = false;
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { rendu = true; return Result.Ok(); });
            vm.LevierParId = id => id == "latency.vrrFlipModelOn" ? levier : null;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };
            // Avant le rendu, la machine porte la valeur de DrDXT ; la relecture d'après dit l'issue.
            vm.VerifierLaPose = _ => rendu ? relu : DrDxt.Application.Tweaks.EtatApplique.Applied;
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            vm.MessageLeviers.Should().Be(dit);
            leviers.Lire().Should().HaveCount(retire ? 0 : 1);
            sortis.Should().HaveCount(retire ? 1 : 0, "le service le reposerait s'il sortait du manifeste encore posé : il y reste");
            vm.LeviersPoses.Should().HaveCount(retire ? 0 : 1, "encore posé, « Annuler » se reclique");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    private static readonly TweakSnapshot SousTensionAmd = new("gpu.undervolt",
        PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 0)).Value!, "-50");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_remise_a_l_usine_de_la_carte_ne_part_que_sur_le_clic(bool parClic)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"usine-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("gpu.undervolt", true, false, SousTensionAmd, null)], null));
            var (helper, levier) = CarteQuiRefuseLOrigine();
            var vm = Vm(Array.Empty<RepairFinding>(), helper, journalLeviers: leviers);
            vm.LevierParId = id => id == "gpu.undervolt" ? levier : null;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());

            await vm.AnnulerLevierAsync("gpu.undervolt", parClic);

            if (parClic)
            {
                await helper.Received(1).ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.RemiseUsine, 0), Arg.Any<CancellationToken>());
                leviers.Lire().Should().BeEmpty("remise à l'usine = rendu : un second « Annuler » ne la relance pas");
                vm.MessageLeviers.Should().Contain(Docteur.T("ADLX.UsineRemise"));
            }
            else
            {
                await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
                leviers.Lire().Should().ContainSingle("rien n'est rendu sans clic : le refus se dit");
            }
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le constat de démarrage : le démarrage a dit le refus ; le bouton remet la carte à l'usine, puis le journal se lève.
    [Fact]
    public async Task Le_bouton_du_demarrage_remet_la_carte_a_l_usine_et_leve_l_essai()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"essai-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(chemin);
            journal.Poser("gpu.undervolt", SousTensionAmd).IsOk.Should().BeTrue();
            var (helper, levier) = CarteQuiRefuseLOrigine();
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.LevierParId = id => id == "gpu.undervolt" ? levier : null;

            vm.RemiseUsineProposee.Should().BeFalse("jamais proposée sans refus au démarrage");
            vm.ProposerRemiseUsine(journal);
            vm.RemiseUsineProposee.Should().BeTrue();
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());

            await vm.RemettreLaCarteALUsineCommand.ExecuteAsync(null);

            await helper.Received(1).ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.RemiseUsine, 0), Arg.Any<CancellationToken>());
            journal.Lire().Value.Should().BeNull("rendu : le démarrage suivant ne retente rien");
            vm.RemiseUsineProposee.Should().BeFalse();
            vm.MessageDemarrage.Should().Be(Docteur.T("ADLX.UsineRemise"));
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Rendu par le levier sans remise à l'usine : « annulé » seulement relu à l'origine. Encore posé, le journal et le bouton
    // restent.
    [Theory]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.NotApplied, "Leviers.Annule", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Applied, "Leviers.PasAnnule", false)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unknown, "Leviers.AnnuleNonRelu", true)]
    public async Task Le_bouton_du_demarrage_ne_dit_annule_que_relu_a_l_origine(DrDxt.Application.Tweaks.EtatApplique relu, string dit, bool leve)
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"essai-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(chemin);
            journal.Poser("gpu.undervolt", SousTensionAmd).IsOk.Should().BeTrue();
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.LevierParId = id => id == "gpu.undervolt" ? levier : null;
            vm.VerifierLaPose = _ => relu;
            vm.ProposerRemiseUsine(journal);

            await vm.RemettreLaCarteALUsineCommand.ExecuteAsync(null);

            vm.MessageDemarrage.Should().Be(dit);
            (journal.Lire().Value is null).Should().Be(leve);
            vm.RemiseUsineProposee.Should().Be(!leve);
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Le journal vit sous %LOCALAPPDATA%, écrivable par tout programme du compte : relu au clic, un inverse qui n'est pas
    // celui du levier ne part pas, même sur le bouton.
    [Fact]
    public async Task Un_essai_modifie_ne_part_pas_sur_le_bouton_du_demarrage()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"essai-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(chemin);
            journal.Poser("gpu.undervolt", SousTensionAmd).IsOk.Should().BeTrue();
            var (helper, levier) = CarteQuiRefuseLOrigine();
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.LevierParId = id => id == "gpu.undervolt" ? levier : null;
            vm.ProposerRemiseUsine(journal);
            journal.Lever();
            journal.Poser("gpu.undervolt", new TweakSnapshot("gpu.undervolt",
                InversesDeTest.Pour("latency.hagsOn"), "x")).IsOk.Should().BeTrue();

            await vm.RemettreLaCarteALUsineCommand.ExecuteAsync(null);

            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            vm.MessageDemarrage.Should().Be("Demarrage.RemiseUsineIntrouvable");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Le réglage est bien annulé sur la machine, mais le journal n'a pas pu être réécrit : le scan
    // suivant le dirait « revenu à la valeur d'avant ». C'est dit tout de suite, plutôt qu'une fausse régression plus tard.
    [Fact]
    public async Task Un_journal_qui_ne_se_reecrit_pas_apres_une_annulation_est_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        var chemin = System.IO.Path.Combine(dossier, "l.json");
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(chemin);
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", InversesDeTest.Pour("latency.vrrFlipModelOn", "origine"), "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            System.IO.File.SetAttributes(chemin, System.IO.FileAttributes.ReadOnly); // se lit, ne se réécrit plus
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.vrrFlipModelOn" ? levier : null;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            await levier.Received(1).RevertAsync(pose);
            vm.MessageLeviers.Should().Contain("Leviers.JournalPasMisAJour");
        }
        finally
        {
            System.IO.File.SetAttributes(chemin, System.IO.FileAttributes.Normal);
            System.IO.Directory.Delete(dossier, true);
        }
    }

    // Après une désinstallation puis une réinstallation, le journal des leviers peut encore dire « posé » alors
    // que la valeur est revenue à l'origine. La carte ne montre que ce qui est encore en place.
    [Fact]
    public void Un_reglage_revenu_a_sa_valeur_d_avant_n_est_pas_montre_comme_pose()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            const string chemin = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
            string Op(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, chemin,
                "HistoricalCaptureEnabled", RegistryValueType.DWord, v)).Value!;
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.gameDvrBackgroundOff", Op("1"), Op("0"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(1u));

            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers, registreGains: registre);
            vm.ChargerLeviersPoses();

            vm.LeviersPoses.Should().BeEmpty("la valeur est celle d'avant DrDXT : rien à annuler");

            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(0u));
            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().ContainSingle("témoin : encore en place, il est montré");

            // Changé depuis par le joueur (ni l'origine, ni la valeur de DrDXT) : pas montré,
            // « Annuler » réécrirait la valeur d'avant par-dessus son choix.
            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(2u));
            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().BeEmpty();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le mode Jeu agit au prochain lancement du jeu, sans redémarrage : son annulation le dit ainsi.
    [Fact]
    public async Task L_annulation_du_mode_Jeu_dit_le_prochain_lancement_du_jeu()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.gameModeOn", InversesDeTest.Pour("latency.gameModeOn", "0"), "1");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.Id.Returns("latency.gameModeOn");
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.gameModeOn");

            vm.MessageLeviers.Should().Be("Leviers.AnnuleAuLancementDuJeu");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Un levier qui ne prend effet qu'au redémarrage le dit ; un levier introuvable (retiré
    // entre-temps) le dit au lieu de ne rien faire ; le message reste visible après la dernière annulation.
    [Fact]
    public async Task Une_annulation_au_redemarrage_ou_introuvable_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.hagsOn");
            vm.MessageLeviers.Should().Be("Leviers.AnnuleAuRedemarrage");

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.hagsOn");
            vm.MessageLeviers.Should().Be("Leviers.Introuvable");
        }
        finally { System.IO.Directory.Delete(dossier, true); }

        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var carte = System.Text.RegularExpressions.Regex.Match(xaml, "<Border x:Name=\"CarteLeviersPoses\"(.*?)</Border>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        carte.Should().Contain("LeviersPoses", "témoin : la carte existe");
        carte.Should().NotContain("MessageLeviers", "dans la carte, le message disparaîtrait avec le dernier levier annulé");
        xaml.Should().Contain("{Binding MessageLeviers}");
    }

    [Fact]
    public async Task Une_annulation_refusee_ne_retire_rien_et_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", InversesDeTest.Pour("latency.vrrFlipModelOn", "origine"), "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail(ErrorCodes.HelperOpFailed, "UAC refusée"));
            vm.LevierParId = _ => levier;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            sortis.Should().BeEmpty();
            leviers.Lire().Should().ContainSingle();
            vm.LeviersPoses.Should().ContainSingle();
            vm.MessageLeviers.Should().Be("Leviers.PasAnnule");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le retrait ne touche que la bascule née de la pose de DrDXT.
    [Theory]
    [InlineData("joueur")]      // DrDXT pose, le joueur coupe puis rallume lui-même : la dernière bascule est la sienne
    [InlineData("ancienne")]    // une vieille bascule jugée pire, puis une nouvelle pose pas encore suivie d'effet
    public async Task Une_bascule_qui_ne_vient_pas_de_la_pose_n_est_jamais_retiree(string cas)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2"), null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var t = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            void Bascule(double h, string v) => vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(t.AddHours(h), v, t.AddHours(h - 0.5), "windows.hags:" + v));
            if (cas == "joueur")
            {
                vm.NoterPoses(["latency.hagsOn"], t.AddHours(-5));
                Bascule(-4, "on"); Bascule(-3, "off"); Bascule(0, "on");
            }
            else
            {
                Bascule(0, "on");                       // le joueur, jugé pire ci-dessous
                vm.NoterPoses(["latency.hagsOn"], t.AddHours(1));
            }
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", t.AddHours(h), t.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty();
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Une seule opération à la fois : pendant l'une (ici le repos de la carte), poser un réglage Expert ou confirmer
    // « Vérifier DrDXT » se grisent, comme les autres boutons qui écrivent.
    [Fact]
    public void Expert_et_la_verification_se_grisent_pendant_une_autre_operation()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.ExpertPeutPoser.Should().BeTrue("témoin : rien ne tourne");
        vm.ConfirmerVerificationCommand.CanExecute(null).Should().BeTrue("témoin : rien ne tourne");

        vm.ReposCarteEnCours = true;

        vm.ExpertPeutPoser.Should().BeFalse();
        vm.ConfirmerVerificationCommand.CanExecute(null).Should().BeFalse();
    }
}
