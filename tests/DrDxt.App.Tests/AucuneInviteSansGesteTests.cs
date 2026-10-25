using DrDxt.Tests.Shared;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Les chemins que le joueur n'a pas déclenchés (au lancement, en veille, après une partie relevée) passaient par le client
/// qui lance le helper : helper arrêté, une invite Windows s'ouvrait seule, en pleine partie ou au démarrage. Chaque chemin
/// tourne ici avec le vrai client qui lance, devant un helper mort : zéro lancement, et l'écran dit ce qui reste à faire.
/// </summary>
public sealed class AucuneInviteSansGesteTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-sans-geste-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private sealed class HelperMort : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result.Fail(ErrorCodes.HelperUnreachable, "tube absent (test)"));
    }

    private int _lancements;

    // Le client de l'App : il lance le helper à la demande (le lanceur compte), devant un helper qui ne répond pas.
    private LaunchOnDemandElevatedClient Client() =>
        new(_ => { _lancements++; return Task.FromResult(Result.Fail(ErrorCodes.ElevationDeclined, "invite refusée (test)")); },
            new HelperMort());

    /// <summary>Un levier qui écrit, comme les vrais, par le client qu'on lui a donné.</summary>
    private sealed class LevierParLeClient(string id, IElevatedClient client) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public bool TakesEffectAfterReboot => true;
        public string SummaryKey => "x";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => throw new NotSupportedException();
        public Task<Result> RevertAsync(TweakSnapshot s)
            => client.ExecuteAsync(PrivilegedOpCodec.Deserialize(s.OriginalValueJson).Value!, CancellationToken.None);
    }

    private static ScanViewModel Vm(IElevatedClient? helper) =>
        ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            helper: helper);

    private string Fichier(string nom) => Path.Combine(_dossier, nom);
    private const string JeuAbsent = "drdxt-jeu-absent-des-tests.exe";
    private static readonly string SansGeste = Docteur.T("Elevation.SansGeste");

    // Au lancement : la protection du ping que le joueur avait laissée allumée.
    [Fact]
    public async Task La_protection_du_ping_rallumee_au_lancement_n_ouvre_aucune_invite()
    {
        var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(Fichier("p.json"));
        memoire.Retenir(new(JeuAbsent, 20_000_000, DateTimeOffset.Now));
        var vm = Vm(Client());
        vm.MemoirePlafond = memoire;

        await vm.RallumerPlafondEnvoiAsync();

        _lancements.Should().Be(0);
        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("Erreur.Generique").And.NotContain(SansGeste);
        memoire.Lire().Should().NotBeNull("le joueur l'avait voulue : le prochain lancement réessaie");
    }

    // En pleine partie : la veille voit le défaut et allume la protection.
    [Fact]
    public async Task La_veille_du_ping_en_pleine_partie_n_ouvre_aucune_invite()
    {
        var vm = Vm(Client());
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 10))
                .Concat(Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(100, 40))).ToList());
        vm.JeuPourLaVeille = () => JeuAbsent;
        vm.VeillePingActive = true;

        await vm.SurveillerLesPartiesAsync(CancellationToken.None);

        _lancements.Should().Be(0);
        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("Erreur.Generique").And.NotContain(SansGeste);
    }

    // Au lancement : la preuve du repos de la carte se conclut sans baisse, et le réglage doit être retiré.
    [Fact]
    public async Task La_preuve_du_repos_conclue_au_lancement_n_ouvre_aucune_invite()
    {
        var vm = Vm(helper: null);
        var levier = new LevierParLeClient("gpu.repos_global_normal", Client());
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(Fichier("preuve.json"));
        attente.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), "normal", [50.0, 50.5, 51.0]));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));

        await vm.ConclureReposCarteAsync(levier, sonde, attente, _ => Task.CompletedTask);

        _lancements.Should().Be(0);
        vm.VerdictReposCarte.Should().StartWith("Carte.Repos.PreuveRetraitRate");
        attente.Charger().Should().NotBeNull("le réglage est toujours posé : la preuve reste");
    }

    // Après une partie relevée : le réglage gardé au repos abîme le jeu, et doit être retiré.
    [Fact]
    public async Task La_surveillance_de_la_carte_apres_une_partie_n_ouvre_aucune_invite()
    {
        var vm = Vm(helper: null);
        var levier = new LevierParLeClient("gpu.repos_global_normal", Client());
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(Fichier("retenu.json"));
        retenu.Garder(new("gpu.repos_global_normal", InversesDeTest.Pour("gpu.repos_global_normal"), null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);

        await vm.SurveillerLaCarteEnJeuAsync(levier, [P(-3, 90), P(-2, 92), P(-1, 91), P(1, 80), P(2, 81), P(3, 80.5)], retenu);

        _lancements.Should().Be(0);
        vm.VerdictReposCarte.Should().StartWith("Carte.Repos.RetraitRate");
        retenu.Charger().Should().NotBeNull("toujours posé : il reste suivi");
    }

    // Après une partie relevée : un levier de DrDXT moins bien en jeu se retire. Le helper a répondu à la vérification puis
    // s'est arrêté (15 min d'inactivité) : le retour arrière ne le relance pas.
    [Fact]
    public async Task Le_retrait_automatique_ne_relance_pas_un_helper_arrete_entre_deux()
    {
        var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(Fichier("l.json"));
        var pose = new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2");
        leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: leviers);
        vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(Fichier("j.json"));
        var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
        vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
        vm.LevierParId = id => id == "latency.hagsOn" ? new LevierParLeClient(id, Client()) : null;
        vm.HelperJoignableSansUac = () => Task.FromResult(true);
        DrDxt.Application.Parties.PartieRelevee P(double h, double low1) =>
            new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);

        await vm.RetirerCeQuiCouteAsync([P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81)]);

        _lancements.Should().Be(0);
        leviers.Lire().Should().ContainSingle("rien n'est rendu : le levier reste au journal, et le retrait se retentera");
    }

    // La capture de la veille est prise en pleine partie : le banc n'y coupe pas de « démarrage » (MesureEnPartie), et un
    // scan, hors de la veille, garde sa recherche du démarrage.
    [Fact]
    public async Task La_capture_de_la_veille_se_mesure_en_pleine_partie()
    {
        bool? vu = null;
        var banc = Substitute.For<IBenchmarkRunner>();
        banc.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => { vu = DrDxt.Measurement.MesureEnPartie.Active; return Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "test"); });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            banc, Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);

        await vm.Invoking(v => v.CapturerSansGesteAsync(4242, TimeSpan.FromSeconds(1), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        vu.Should().BeTrue();
        DrDxt.Measurement.MesureEnPartie.Active.Should().BeFalse("la portée se referme avec la capture");
    }

    // En pleine partie : la capture de la veille des parties. Sans PresentMon, le banc l'installe par le helper ; sans clic,
    // il ne le lance pas, et la capture échoue en le disant (le prochain scan l'installera).
    [Fact]
    public async Task La_capture_de_la_veille_n_installe_pas_PresentMon_par_une_invite()
    {
        var sansPresentMon = Substitute.For<IBenchmarkRunner>();
        sansPresentMon.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "PresentMon absent (test)"));
        var banc = new DrDxt.Measurement.SelfHealingBenchmarkRunner(sansPresentMon, Client());
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            banc, Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);

        await vm.Invoking(v => v.CapturerSansGesteAsync(4242, TimeSpan.FromSeconds(1), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("la capture échoue, la veille note pourquoi");
        _lancements.Should().Be(0);

        await banc.RunAsync(4242, TimeSpan.FromSeconds(1), 1, CancellationToken.None);
        _lancements.Should().Be(1, "témoin : hors de la veille (un scan, un clic), le banc installe PresentMon par le helper");
    }

    // Au lancement : l'essai d'une session interrompue. Sans helper, rien ne s'ouvre ; le bouton le rend sur un clic.
    [Fact]
    public async Task L_essai_interrompu_se_rend_sur_un_clic_et_jamais_seul_au_lancement()
    {
        var client = Client();
        var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(Fichier("essai.json"));
        journal.Poser("latency.hagsOn", new TweakSnapshot("latency.hagsOn", InversesDeTest.Pour("latency.hagsOn", "1"), "2")).IsOk.Should().BeTrue();
        var catalogue = Substitute.For<ITweakCatalog>();
        catalogue.Get("latency.hagsOn").Returns(Result<ITweak>.Ok(new LevierParLeClient("latency.hagsOn", client)));
        var vm = Vm(helper: null);

        await vm.RendreSessionInterrompueAsync(journal, catalogue);

        _lancements.Should().Be(0);
        journal.Lire().Value.Should().NotBeNull("rien n'est rendu : l'essai reste au journal");
        vm.RenduEssaiPropose.Should().BeTrue();
        vm.MessageDemarrage.Should().Contain("Erreur.Generique").And.NotContain(SansGeste);

        await vm.RendreEssaiInterrompuCommand.ExecuteAsync(null);
        _lancements.Should().Be(1, "le clic du joueur, lui, peut lancer le helper");
    }
}
