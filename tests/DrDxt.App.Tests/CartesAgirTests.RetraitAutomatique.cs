using DrDxt.Tests.Shared;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Le retrait automatique d'un levier qui coûte en jeu.</summary>
public sealed partial class CartesAgirTests
{
    // Un levier de DrDXT moins bien en jeu se retire seul ; un réglage basculé par le
    // joueur (absent du journal des leviers) n'est jamais touché.
    [Fact]
    public async Task Un_levier_de_DrDXT_moins_bien_en_jeu_se_retire_seul()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            var dits = await vm.RetirerCeQuiCouteAsync(pire);

            dits.Should().ContainSingle().Which.Should().Be("Changement.RetireAuRedemarrage");
            await levier.Received(1).RevertAsync(pose);
            leviers.Lire().Should().BeEmpty("retiré, il n'est plus « gardé par DrDXT »");

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty("déjà retiré : plus au journal des leviers");

            // Retiré de la machine mais resté au manifeste signé : la carte le dit.
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));
            (await vm.RetirerCeQuiCouteAsync(pire)).Should().Equal("Changement.RetireAuRedemarrage", "Changement.PasSortiDuManifeste");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Un jeu tourne encore (une partie relancée aussitôt, un second jeu) : rien ne s'écrit sans clic pendant une partie, le
    // retrait non plus. Il attend une fin de partie jeu fermé ; le levier reste au journal, « Annuler » le retire d'un clic.
    [Fact]
    public async Task Le_retrait_automatique_attend_que_le_jeu_soit_ferme()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };
            var jeuOuvert = true;
            vm.JeuOuvertPourLeTest = () => jeuOuvert;

            await vm.RetirerCeQuiCouteAsync(pire);

            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().ContainSingle("toujours posé, toujours annulable d'un clic");

            jeuOuvert = false;
            await vm.RetirerCeQuiCouteAsync(pire);
            await levier.Received(1).RevertAsync(pose);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // HAGS est un levier de latence : il coûte un peu d'images et baisse la latence. Jugé sur le 1 % low, ce gain passait pour
    // « moins bien en jeu » et le retrait automatique le défaisait. Ses parties portent la latence : il se juge sur elle.
    [Fact]
    public async Task Un_levier_de_latence_qui_baisse_la_latence_ne_se_retire_pas_sur_les_images()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1, double ms) => new("cod.exe", quand.AddHours(h),
                quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound, LatenceAffichageMedianMs: ms);
            var parties = new[] { P(-30, 95, 30), P(-20, 96, 31), P(-10, 94, 29), P(10, 80, 20), P(20, 82, 21), P(30, 81, 19) };

            (await vm.RetirerCeQuiCouteAsync(parties)).Should().BeEmpty("la latence a nettement baissé");
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().ContainSingle();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le calmeur de voisins se juge comme un levier. Sa bascule est datée au journal (visible sur
    // la carte), « Arrêter » reste cliquable pendant qu'il tourne, et un « moins bien en jeu » l'éteint seul ; « mieux »
    // le laisse. Aucun vrai processus : priorités factices, aucun jeu en cours.
    [Theory]
    [InlineData(80, true)]
    [InlineData(110, false)]
    public async Task Le_calmeur_de_voisins_est_date_juge_et_retire_seul_s_il_coute(double low1Apres, bool retire)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"calmeur-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            // Le libellé porte la valeur : sinon l'arrêt, même libellé dans la minute, serait pris pour un doublon.
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, loc: k => k == "Changement.Reglage" ? "{0}:{1}>{2}" : k);
            vm.Priorites = new PrioritesFactices();
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            _ = vm.BasculerCalmerVoisinsCommand.ExecuteAsync(null);

            vm.CalmerVoisinsActif.Should().BeTrue();
            vm.BasculerCalmerVoisinsCommand.CanExecute(null).Should().BeTrue("« Arrêter » se clique pendant qu'il tourne");
            var pose = vm.JournalChangements.Lire().Should().ContainSingle().Which;
            pose.Cle.Should().Be("voisins.calmer:on");
            vm.ChangementsEnAttente.Should().Contain(l => l.EnJeu == "Changement.Tableau.SansAvant", "la bascule se lit sur la carte (pas encore de parties ; 08/10 : en tableau)");

            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", pose.Quand.AddHours(h),
                pose.Quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, low1Apres), P(20, low1Apres + 2), P(30, low1Apres + 1) };

            var dits = await vm.RetirerCeQuiCouteAsync(parties);

            vm.CalmerVoisinsActif.Should().Be(!retire);
            if (retire)
            {
                dits.Should().ContainSingle().Which.Should().Be("Changement.Retire");
                vm.JournalChangements.Lire().First().Cle.Should().Be("voisins.calmer:off", "l'arrêt est daté à son tour");
            }
            else
            {
                dits.Should().BeEmpty();
                await vm.BasculerCalmerVoisinsCommand.ExecuteAsync(null);
                vm.CalmerVoisinsActif.Should().BeFalse("annulable depuis l'écran");
                vm.JournalChangements.Lire().First().Cle.Should().Be("voisins.calmer:off");
            }
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Les parties relevées nomment un voisin gourmand : le calmeur est proposé d'office, sans être
    // allumé ; exclu par le joueur, il ne l'est plus, et l'exclusion est gardée.
    // Aucune partie relevée : « allume le relevé » seulement quand le joueur l'a éteint ; allumé (le cas par défaut), la phrase
    // dit que rien n'est encore lu.
    [Fact]
    public void Sans_partie_la_phrase_suit_l_etat_du_releve()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"proposition-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var memoire = new DrDxt.Application.Parties.PartiesMemorisees(System.IO.Path.Combine(dossier, "c.json"), System.IO.Path.Combine(dossier, "p.json"));
            memoire.Eteindre();
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.MemoireParties = memoire;

            vm.PropositionCalmer.Should().Be("Voisins.Proposition.NonLu");
            memoire.Allumer();
            vm.PropositionCalmer.Should().Be("Voisins.Proposition.PasEncore");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    [Fact]
    public void Un_voisin_gourmand_des_parties_fait_proposer_le_calmeur_sauf_s_il_est_exclu()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"proposition-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var memoire = new DrDxt.Application.Parties.PartiesMemorisees(System.IO.Path.Combine(dossier, "c.json"), System.IO.Path.Combine(dossier, "p.json"));
            var t = DateTimeOffset.Now.AddHours(-1);
            memoire.Ajouter(new DrDxt.Application.Parties.PartieRelevee("cod.exe", t, t.AddMinutes(20), 6, 120, 90, BottleneckState.CpuBoundOrAppBound,
                Voisins: [new("obs64", 60, 3)]));
            memoire.Eteindre(); // le relevé est allumé par défaut : coupé, aucune veille ne démarre dans le test
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.Priorites = new PrioritesFactices();
            vm.FichierExclus = new FichierJsonAtomique(System.IO.Path.Combine(dossier, "x.json"), "X");
            vm.MemoireParties = memoire;

            vm.ChargerParties();

            vm.CalmeurPropose.Should().BeTrue();
            vm.PropositionCalmer.Should().Be("Voisins.Proposition");
            vm.CalmerVoisinsActif.Should().BeFalse("proposé, jamais allumé sans le clic du joueur");

            vm.VoisinsExclus = "OBS64.exe";
            vm.CalmeurPropose.Should().BeFalse();
            vm.PropositionCalmer.Should().Be("Voisins.Proposition.Aucun");
            System.IO.File.ReadAllText(System.IO.Path.Combine(dossier, "x.json")).Should().Be("OBS64.exe");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // HAGS posé, deux parties, puis le joueur baisse sa limite au BIOS (B) et tout chute.
    // Jugé sans borne, l'après de HAGS prenait les parties de B et HAGS était annulé à cause du BIOS. Borné comme à
    // l'écran : deux parties seulement, « pas encore jugé », rien n'est retiré.
    [Fact]
    public async Task Le_retrait_automatique_ne_juge_pas_un_levier_sur_les_parties_du_changement_suivant()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-borne-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand.AddHours(25), "PL1", quand.AddHours(24), "cpu.pl1:65"));
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 93), P(20, 92), P(30, 80), P(40, 82), P(50, 81) };

            (await vm.RetirerCeQuiCouteAsync(parties)).Should().BeEmpty();
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le retrait part à l'ouverture de l'App, sans geste. Si le helper ne tourne pas déjà,
    // l'annuler ferait surgir une invite UAC que le joueur n'a pas demandée : rien n'est retiré, et la carte dit d'annuler.
    [Fact]
    public async Task Sans_helper_deja_lance_le_retrait_ne_fait_surgir_aucune_invite_et_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-uac-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.RequiresElevation.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            vm.HelperJoignableSansUac = () => Task.FromResult(false);
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().ContainSingle().Which.Should().Be("Changement.ARetirer");
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().ContainSingle("rien n'est retiré : le bouton Annuler reste là");

            vm.HelperJoignableSansUac = () => Task.FromResult(true);
            (await vm.RetirerCeQuiCouteAsync(pire)).Should().ContainSingle().Which.Should().Be("Changement.Retire", "helper déjà lancé : le retrait part, sans invite");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Un retrait refusé laisse le levier posé, toujours listé : l'écran dit qu'il n'est pas parti.
    [Fact]
    public async Task Un_retrait_refuse_se_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-rate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.RequiresElevation.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("REFUS", "pilote"));
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            vm.HelperJoignableSansUac = () => Task.FromResult(true);
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().ContainSingle().Which.Should().Be("Changement.RetraitRate");
            leviers.Lire().Should().ContainSingle("témoin : il reste listé, le bouton Annuler reste là");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le fichier d'échange remis par DrDXT protège des plantages, il n'annonce aucune image.
    // « Moins bien en jeu » sur trois parties ne le retire pas : l'annuler re-désactiverait le fichier d'échange.
    [Fact]
    public async Task Le_fichier_d_echange_pose_par_DrDXT_ne_se_retire_pas_sur_les_images()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-pf-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            const string Mm = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
            registre.ReadMultiString(RegistryHiveKind.LocalMachine, Mm, "PagingFiles").Returns(Result<string?>.Ok(""));
            var helper = Substitute.For<IElevatedClient>();
            helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
            var levier = new DrDxt.Application.Tweaks.FixIt.SanePagefileTweak(registre, helper);
            var pose = (await levier.ApplyAsync()).Value!;

            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(levier.Id, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses([levier.Id], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "PF", quand.AddHours(-1), "memoire.fichier_echange:on"));
            vm.LevierParId = id => id == levier.Id ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };
            helper.ClearReceivedCalls();

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty();
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
            leviers.Lire().Should().ContainSingle();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Le retrait automatique suit la même règle : « Retiré » ne se dit que relu à l'origine. Changé depuis hors de DrDXT
    // (avant le retrait, la machine ne porte ni la valeur posée ni l'origine), rien n'est réécrit par-dessus.
    [Theory]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.NotApplied, "Changement.Retire", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Applied, "Changement.RetraitRate", false)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unknown, "Changement.RetireNonRelu", true)]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Drifted, null, false, true)]
    public async Task Un_retrait_automatique_n_est_dit_fait_que_relu_a_l_origine(DrDxt.Application.Tweaks.EtatApplique relu, string? dit, bool retire,
        bool changeDepuis = false)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-relu-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            var rendu = false;
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { rendu = true; return Result.Ok(); });
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };
            // Avant le retrait, la machine porte la valeur de DrDXT (ou une autre, changée depuis) ; la relecture d'après dit l'issue.
            vm.VerifierLaPose = _ => rendu || changeDepuis ? relu : DrDxt.Application.Tweaks.EtatApplique.Applied;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            var dits = await vm.RetirerCeQuiCouteAsync(pire);

            rendu.Should().Be(!changeDepuis);
            if (dit is null) dits.Should().BeEmpty();
            else dits.Should().ContainSingle().Which.Should().Be(dit);
            leviers.Lire().Should().HaveCount(retire ? 0 : 1, "encore posé, il reste à retirer");
            sortis.Should().HaveCount(retire ? 1 : 0);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
