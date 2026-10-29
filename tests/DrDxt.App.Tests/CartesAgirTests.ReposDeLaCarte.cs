using DrDxt.Tests.Shared;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Le repos de la carte graphique : sa preuve, sa surveillance en jeu et son retrait.</summary>
public sealed partial class CartesAgirTests
{
    // Au lancement, une preuve du repos commencée avant un redémarrage se
    // conclut, et la carte dit ce que la mesure a décidé. Rien n'attendait : aucune phrase.
    [Theory]
    [InlineData(14.0, "Carte.Repos.Prouve")]
    [InlineData(55.0, "Carte.Repos.PasDeGain")]
    public async Task Au_lancement_la_preuve_du_repos_se_conclut_et_se_dit(double apresW, string cle)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        attente.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, [55.0, 55.5, 56.0]));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(apresW, null));

        await vm.ConclureReposCarteAsync(levier, sonde, attente, _ => Task.CompletedTask);

        vm.VerdictReposCarte.Should().StartWith(cle);
        (await Task.FromResult(attente.Charger())).Should().BeNull();
    }

    // Pas de baisse, et le helper refuse le retrait : l'écran ne doit pas dire « retiré, la carte est revenue comme
    // avant », ni effacer la preuve en attente. Le réglage reste suivi (la preuve reste) et l'échec est dit.
    [Fact]
    public async Task Un_retrait_rate_apres_la_preuve_garde_le_reglage_suivi_et_le_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail(ErrorCodes.HelperOpFailed, "helper refusé"));
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        attente.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), "normal", [50.0, 50.5, 51.0]));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));

        await vm.ConclureReposCarteAsync(levier, sonde, attente, _ => Task.CompletedTask);

        vm.VerdictReposCarte.Should().StartWith("Carte.Repos.PreuveRetraitRate");
        attente.Charger().Should().NotBeNull("le réglage est toujours posé : la preuve reste, le retrait se retentera");
    }

    // Un réglage gardé au repos reste sous surveillance en jeu. Trois parties d'après, toutes sous les
    // trois d'avant sur le même jeu : il est retiré et c'est dit. Pas encore trois : rien ne bouge.
    [Theory]
    [InlineData(80.0, true)]
    [InlineData(91.0, false)]
    public async Task Un_reglage_garde_qui_abime_le_jeu_est_retire(double low1Apres, bool retire)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retenu-{Guid.NewGuid():N}.json"));
        retenu.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);
        var parties = new[] { P(-3, 90), P(-2, 92), P(-1, 91), P(1, low1Apres), P(2, low1Apres + 1), P(3, low1Apres + 0.5) };

        await vm.SurveillerLaCarteEnJeuAsync(levier, parties, retenu);

        if (retenu.Charger() is null != retire) throw new Xunit.Sdk.XunitException("le retenu doit disparaître si et seulement si le réglage est retiré");
        if (retire)
        {
            await levier.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
            vm.VerdictReposCarte.Should().StartWith("Carte.Repos.PireEnJeu");
        }
        else await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    // Un jeu tourne encore après la partie relevée : rien ne s'écrit sans clic pendant une partie. Le retrait attend une fin de
    // partie jeu fermé ; le réglage reste suivi.
    [Fact]
    public async Task Un_reglage_garde_qui_abime_le_jeu_attend_que_le_jeu_soit_ferme()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retenu-{Guid.NewGuid():N}.json"));
        retenu.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);
        var parties = new[] { P(-3, 90), P(-2, 92), P(-1, 91), P(1, 80), P(2, 81), P(3, 80.5) };
        var jeuOuvert = true;
        vm.JeuOuvertPourLeTest = () => jeuOuvert;

        await vm.SurveillerLaCarteEnJeuAsync(levier, parties, retenu);

        await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        retenu.Charger().Should().NotBeNull("toujours posé, toujours suivi");

        jeuOuvert = false;
        await vm.SurveillerLaCarteEnJeuAsync(levier, parties, retenu);
        await levier.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
    }

    // Le réglage gardé a été changé depuis hors de DrDXT : les parties « pires » ne le font pas réécrire. Il n'est plus suivi,
    // et c'est dit. Retiré mais non relu : « retiré » ne se dit pas seul.
    [Theory]
    [InlineData("changé", false, "Carte.Repos.ChangeDepuis")]
    [InlineData("non relu", true, "Carte.Repos.PireEnJeuNonRelu")]
    public async Task Un_reglage_garde_ne_se_retire_que_sur_la_valeur_de_DrDXT_et_se_relit(string cas, bool rendu, string dit)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        var ecrit = false;
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { ecrit = true; return Result.Ok(); });
        vm.VerifierLaPose = _ => cas == "changé" ? DrDxt.Application.Tweaks.EtatApplique.Drifted
            : ecrit ? DrDxt.Application.Tweaks.EtatApplique.Unknown : DrDxt.Application.Tweaks.EtatApplique.Applied;
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retenu-{Guid.NewGuid():N}.json"));
        retenu.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);

        await vm.SurveillerLaCarteEnJeuAsync(levier, [P(-3, 90), P(-2, 92), P(-1, 91), P(1, 80), P(2, 81), P(3, 80.5)], retenu);

        ecrit.Should().Be(rendu);
        retenu.Charger().Should().BeNull();
        vm.VerdictReposCarte.Should().StartWith(dit);
    }

    // Un retrait refusé (helper qui dit non) laisse « Normal » posé : effacer le retenu et dire « retiré »
    // ferait que plus rien ne le rendrait. Le retenu reste, et l'échec est dit.
    [Fact]
    public async Task Un_retrait_rate_garde_le_reglage_suivi_et_le_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail(ErrorCodes.HelperOpFailed, "helper refusé"));
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retenu-{Guid.NewGuid():N}.json"));
        retenu.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);
        var parties = new[] { P(-3, 90), P(-2, 92), P(-1, 91), P(1, 80), P(2, 81), P(3, 80.5) };

        await vm.SurveillerLaCarteEnJeuAsync(levier, parties, retenu);

        retenu.Charger().Should().NotBeNull("le réglage est toujours posé : il reste suivi");
        vm.VerdictReposCarte.Should().StartWith("Carte.Repos.RetraitRate");
    }

    // Le geste sur la page Carte graphique. Maintenir mesure le repos, écrit « Normal » et garde la
    // preuve ; la carte dit qu'elle se jugera au prochain redémarrage. Un levier qui refuse : rien d'attendu, et dit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Maintenir_mesure_ecrit_et_met_la_preuve_en_attente(bool ecrit)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.ApplyAsync().Returns(ecrit
            ? Result<TweakSnapshot>.Ok(new TweakSnapshot("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal")))
            : Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "non"));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        vm.LevierReposCarte = levier;
        vm.SondeCarte = sonde;
        vm.AttenteReposCarte = attente;
        vm.AttendreCarte = _ => Task.CompletedTask;

        await vm.RendreLeReposALaCarteCommand.ExecuteAsync(null);

        vm.ReposCarteEnAttente.Should().Be(ecrit);
        (attente.Charger() is not null).Should().Be(ecrit);
        if (!ecrit) vm.ErreurReposCarte.Should().Be("Carte.Repos.Refuse");
        vm.ReposCarteEnCours.Should().BeFalse();
    }

    // Le helper n'a pas répondu à l'écriture : le réglage a pu être posé, « rien n'a été posé » serait faux. La preuve qui ne
    // se garde pas sur le disque ne plante pas l'App : le réglage reste au journal du helper, qui le rendra, et c'est dit.
    [Theory]
    [InlineData(false, "Erreur.Generique")]
    [InlineData(true, "Carte.Repos.PreuveNonGardee")]
    public async Task Un_repos_de_carte_incertain_ou_non_garde_le_dit_sans_planter(bool preuveSurUnDossier, string dit)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.ApplyAsync().Returns(preuveSurUnDossier
            ? Result<TweakSnapshot>.Ok(new TweakSnapshot("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal")))
            : Result<TweakSnapshot>.Fail(ErrorCodes.HelperNoReply, "pas de réponse"));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}");
        if (preuveSurUnDossier) System.IO.Directory.CreateDirectory(chemin);
        try
        {
            vm.LevierReposCarte = levier;
            vm.SondeCarte = sonde;
            vm.AttenteReposCarte = new DrDxt.Application.Tweaks.PreuvesEnAttente(chemin);
            vm.AttendreCarte = _ => Task.CompletedTask;

            await vm.RendreLeReposALaCarteCommand.ExecuteAsync(null);

            vm.ErreurReposCarte.Should().Be(dit);
            vm.ReposCarteEnAttente.Should().BeFalse();
            vm.ReposCarteEnCours.Should().BeFalse();
        }
        finally { if (preuveSurUnDossier) System.IO.Directory.Delete(chemin, true); }
    }

    // Le repos se mesure sans jeu : jeu ouvert, les lectures « avant » seraient celles de la charge, et la preuve du lancement
    // suivant afficherait un faux chiffre. Rien n'est mesuré ni écrit, et c'est dit.
    [Fact]
    public async Task Le_repos_de_la_carte_ne_se_mesure_pas_jeu_ouvert()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, jeu: 4242);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        var sonde = Substitute.For<IHardwareProbe>();
        vm.LevierReposCarte = levier;
        vm.SondeCarte = sonde;
        vm.AttenteReposCarte = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        vm.AttendreCarte = _ => Task.CompletedTask;

        await vm.RendreLeReposALaCarteCommand.ExecuteAsync(null);

        await levier.DidNotReceive().ApplyAsync();
        sonde.DidNotReceive().Lire();
        vm.ErreurReposCarte.Should().Be("Carte.Repos.JeuOuvert");
    }

    [Fact]
    public async Task Sans_preuve_en_attente_rien_ne_se_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");

        await vm.ConclureReposCarteAsync(levier, Substitute.For<IHardwareProbe>(),
            new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json")), _ => Task.CompletedTask);

        vm.VerdictReposCarte.Should().BeNull();
    }

    // Comme le vrai levier : l'origine (performances maximales) et la valeur posée (Normal), toutes deux des réglages DRS.
    private static TweakSnapshot ReposPose => new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal", "1"),
        InversesDeTest.Pour("gpu.repos_global_normal", "0"));

    // Prouvé au repos, le réglage global de la carte reste : comme tout levier gardé, il entre au journal des leviers (« Annuler »
    // dans « Ce que DrDXT a posé ») et au manifeste signé (la désinstallation le rend).
    [Fact]
    public async Task Le_repos_prouve_est_garde_comme_un_levier_annulable_et_rendu_a_la_desinstallation()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repos-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var manifeste = Substitute.For<IDesiredStateStore>();
            manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Task.FromResult(Result.Ok()));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers, manifeste: manifeste);
            vm.ReglagesDeLaCarte = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(dossier, "r.json"));
            var levier = Substitute.For<DrDxt.Core.ITweak>();
            levier.Id.Returns("gpu.repos_global_normal");
            var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(dossier, "p.json"));
            attente.Garder(new(ReposPose.TweakId, ReposPose.OriginalValueJson, ReposPose.AppliedValueJson, [55.0, 55.5, 56.0]));
            var sonde = Substitute.For<IHardwareProbe>();
            sonde.Lire().Returns(new HardwareReading(14.0, null));

            await vm.ConclureReposCarteAsync(levier, sonde, attente, _ => Task.CompletedTask);

            leviers.Lire().Should().ContainSingle(s => s.TweakId == "gpu.repos_global_normal");
            await manifeste.Received().CommitAsync(Arg.Is<DesiredState>(d => d.RevertOnly!.Any(t => t.TweakId == "gpu.repos_global_normal")));
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // « Annuler » trouve le repos de la carte, qui n'est pas au catalogue des leviers, et efface son suivi en jeu : sinon la
    // surveillance le croirait changé depuis par le joueur.
    [Fact]
    public async Task Annuler_le_repos_de_la_carte_le_rend_et_efface_son_suivi()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repos-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(ReposPose.TweakId, true, false, ReposPose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var retenus = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(dossier, "r.json"));
            retenus.Garder(new(ReposPose.TweakId, ReposPose.OriginalValueJson, ReposPose.AppliedValueJson, DateTimeOffset.Now));
            vm.ReglagesDeLaCarte = retenus;
            var levier = Substitute.For<DrDxt.Core.ITweak>();
            levier.Id.Returns("gpu.repos_global_normal");
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierReposCarte = levier;
            vm.LevierParId = _ => null;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());

            await vm.AnnulerLevierAsync("gpu.repos_global_normal", parClic: false);

            await levier.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().BeEmpty();
            retenus.Charger().Should().BeNull("plus rien à surveiller en jeu");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Retiré par la surveillance en jeu, il sort aussi du journal des leviers et du manifeste.
    [Fact]
    public async Task Le_repos_retire_par_la_surveillance_sort_du_journal_et_du_manifeste()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repos-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(ReposPose.TweakId, true, false, ReposPose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };
            var levier = Substitute.For<DrDxt.Core.ITweak>();
            levier.Id.Returns("gpu.repos_global_normal");
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(dossier, "r.json"));
            retenu.Garder(new(ReposPose.TweakId, ReposPose.OriginalValueJson, ReposPose.AppliedValueJson, le));
            DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
                new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);

            await vm.SurveillerLaCarteEnJeuAsync(levier, [P(-3, 90), P(-2, 92), P(-1, 91), P(1, 80), P(2, 81), P(3, 80.5)], retenu);

            leviers.Lire().Should().BeEmpty();
            sortis.Should().Equal("gpu.repos_global_normal");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
