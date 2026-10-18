using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le mode Expert de la carte, sur la page Graphique. Le décalage ne part qu'avec l'accord du
/// joueur, jamais pendant une partie ; gardé, il vit tant que DrDXT est ouvert ; « Rendre » remet l'origine.
/// </summary>
public sealed class CarteExpertTests
{
    private sealed class Essai : IEssaiCarte
    {
        public int Rendus, Liberes;
        public event Action? BailPerdu;
        public void PerdreLeBail() => BailPerdu?.Invoke();
        public Task<Result> RendreAsync() { Rendus++; return Task.FromResult(Result.Ok()); }
        public ValueTask DisposeAsync() { Liberes++; return ValueTask.CompletedTask; }
    }

    private static ScanViewModel Vm(bool jeu = false, Func<string, string>? localiser = null)
    {
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.JeuOuvert().Returns(jeu);
        return ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(),
            localiser ?? (k => k), gameLocator: locator);
    }

    // Quelques secondes de charge sans décrocher ne prouvent pas la stabilité en jeu : le texte dit la durée réelle du test.
    // Le ventilateur n'a pas de test de stabilité : il ne s'en dit aucun.
    [Fact]
    public async Task Garde_dit_la_duree_du_test_de_stabilite_et_rien_pour_le_ventilateur()
    {
        var vm = Vm(localiser: k => k == "Carte.Expert.Garde" ? "tenu {0} s" : k);
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(new Essai()));
        vm.ExpertAccord = true;
        await vm.PoserCarteExpertCommand.ExecuteAsync(null);
        vm.ExpertEtat.Should().Be($"tenu {DrDxt.Application.Tweaks.Hardware.GpuStabilityTester.DureeDeCharge.TotalSeconds} s");

        var autre = Vm(localiser: k => k == "Carte.Expert.Garde" ? "tenu {0} s" : k);
        autre.PoserVentilateurExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(new Essai()));
        autre.ExpertAccord = true;
        await autre.PoserVentilateurExpertCommand.ExecuteAsync(null);
        autre.ExpertEtat.Should().Be("Carte.Expert.GardeVentilateur");
    }

    // Le helper meurt pendant que le ventilateur est fixé : le retour à l'automatique est tenté une fois, refusé, et l'écran le
    // dit au lieu de rester sur « Gardé ».
    private sealed class HelperMort : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result.Fail(ErrorCodes.HelperUnreachable, "tube absent (test)"));
    }

    [Fact]
    public async Task Un_ventilateur_reste_fixe_faute_de_helper_le_dit_sur_la_carte()
    {
        var vm = Vm();
        var helper = new HelperMort();
        var bail = new HeartbeatSender(helper, VentilateurExpert.IdBail, 120);
        await using var essai = new EssaiVentilateur(helper, Substitute.For<DrDxt.Application.Autopilot.IRevertEnAttente>(),
            new RestoreGpuFanAutoOp(0), bail);
        vm.PoserVentilateurExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(essai));
        vm.ExpertAccord = true;
        await vm.PoserVentilateurExpertCommand.ExecuteAsync(null);
        vm.ExpertGarde.Should().BeTrue();

        for (var i = 0; i < 5; i++) await bail.PulseAsync(CancellationToken.None);
        await essai.RenduApresPerte!;

        vm.ExpertEtat.Should().Be(Docteur.T("GX.VentilateurResteFixe", "Erreur.Generique"), "la raison lisible, jamais le message du helper");
        vm.ExpertGarde.Should().BeTrue("la vitesse fixe est restée : « Rendre » reste proposé");
    }

    [Fact]
    public void La_section_reste_cachee_tant_qu_aucun_scan_n_a_vu_un_PC_fixe_a_carte_dediee()
    {
        var vm = Vm();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(new Essai()));
        vm.ExpertVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Sans_accord_rien_ne_part()
    {
        var vm = Vm();
        var appels = 0;
        vm.PoserCarteExpert = _ => { appels++; return Task.FromResult(Result<IEssaiCarte>.Ok(new Essai())); };

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        appels.Should().Be(0);
        vm.ExpertEtat.Should().Be("Carte.Expert.AccordRequis");
    }

    [Fact]
    public async Task Jeu_ouvert_rien_ne_part()
    {
        var vm = Vm(jeu: true);
        var appels = 0;
        vm.PoserCarteExpert = _ => { appels++; return Task.FromResult(Result<IEssaiCarte>.Ok(new Essai())); };
        vm.ExpertAccord = true;

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        appels.Should().Be(0);
        vm.ExpertEtat.Should().Be("Carte.Expert.JeuEnCours");
    }

    [Fact]
    public async Task Pose_avec_accord_puis_rendre_remet_l_origine()
    {
        var vm = Vm();
        var essai = new Essai();
        int? demande = null;
        vm.PoserCarteExpert = mhz => { demande = mhz; return Task.FromResult(Result<IEssaiCarte>.Ok(essai)); };
        vm.ExpertAccord = true;
        vm.ExpertDecalageMhz = 50;

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        demande.Should().Be(50);
        vm.ExpertGarde.Should().BeTrue();
        vm.ExpertEtat.Should().Be("Carte.Expert.Garde");

        await vm.RendreCarteExpertCommand.ExecuteAsync(null);

        essai.Rendus.Should().Be(1);
        essai.Liberes.Should().Be(1, "le bail s'arrête une fois l'origine rendue");
        vm.ExpertGarde.Should().BeFalse();
        vm.ExpertEtat.Should().Be("Carte.Expert.Rendu");
    }

    [Fact]
    public async Task Une_carte_instable_est_dite_rendue()
    {
        var vm = Vm();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Fail(ErrorCodes.TweakUnstable, "instable"));
        vm.ExpertAccord = true;
        vm.ExpertDecalageMhz = 50;

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        vm.ExpertGarde.Should().BeFalse();
        vm.ExpertEtat.Should().Be("Carte.Expert.Instable");
    }

    [Fact]
    public async Task Instable_et_retour_rate_n_est_jamais_dit_rendu()
    {
        var vm = Vm();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Fail(ErrorCodes.TweakPartiallyApplied, "retour raté"));
        vm.ExpertAccord = true;
        vm.ExpertDecalageMhz = 50;

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        vm.ExpertEtat.Should().StartWith("Carte.Expert.RenduRate").And.NotContain("Instable");
    }

    [Fact]
    public async Task Le_bail_perdu_est_dit_et_l_ecran_ne_dit_plus_garde()
    {
        var vm = Vm();
        var essai = new Essai();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(essai));
        vm.ExpertAccord = true;
        vm.ExpertDecalageMhz = 50;
        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        essai.PerdreLeBail();

        vm.ExpertGarde.Should().BeFalse();
        vm.ExpertEtat.Should().Be("Carte.Expert.RenduParLeHelper");
    }

    [Fact]
    public async Task A_la_fermeture_l_essai_garde_est_rendu()
    {
        var vm = Vm();
        var essai = new Essai();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Ok(essai));
        vm.ExpertAccord = true;
        vm.ExpertDecalageMhz = 50;
        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        await vm.FermerCarteExpertAsync();

        essai.Rendus.Should().Be(1);
        essai.Liberes.Should().Be(1);
    }

    [Fact]
    public async Task Un_refus_dit_sa_raison()
    {
        var vm = Vm();
        vm.PoserCarteExpert = _ => Task.FromResult(Result<IEssaiCarte>.Fail(ErrorCodes.HelperOpFailed, "bail refusé"));
        vm.ExpertAccord = true;

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);

        vm.ExpertGarde.Should().BeFalse();
        vm.ExpertEtat.Should().Contain("Carte.Expert.Refuse").And.Contain("Erreur.Generique").And.NotContain("bail refusé");
    }

    // Le ventilateur partage l'accord, la règle du jeu ouvert et « Rendre » de la fréquence ; un seul
    // essai gardé à la fois.
    [Fact]
    public async Task Le_ventilateur_part_avec_l_accord_sa_vitesse_et_bloque_un_second_essai()
    {
        var vm = Vm();
        var vitesses = new List<int>();
        var essai = new Essai();
        vm.PoserVentilateurExpert = pct => { vitesses.Add(pct); return Task.FromResult(Result<IEssaiCarte>.Ok(essai)); };
        var frequences = 0;
        vm.PoserCarteExpert = _ => { frequences++; return Task.FromResult(Result<IEssaiCarte>.Ok(new Essai())); };

        await vm.PoserVentilateurExpertCommand.ExecuteAsync(null);
        vitesses.Should().BeEmpty("sans accord rien ne part");

        vm.ExpertAccord = true;
        vm.ExpertVentilateurPct = 80;
        await vm.PoserVentilateurExpertCommand.ExecuteAsync(null);
        vitesses.Should().Equal(80);
        vm.ExpertGarde.Should().BeTrue();

        await vm.PoserCarteExpertCommand.ExecuteAsync(null);
        frequences.Should().Be(0, "un seul essai gardé à la fois");

        await vm.RendreCarteExpertCommand.ExecuteAsync(null);
        essai.Rendus.Should().Be(1);
        vm.ExpertGarde.Should().BeFalse();
    }

    [Fact]
    public async Task Jeu_ouvert_le_ventilateur_ne_part_pas()
    {
        var vm = Vm(jeu: true);
        var appels = 0;
        vm.PoserVentilateurExpert = _ => { appels++; return Task.FromResult(Result<IEssaiCarte>.Ok(new Essai())); };
        vm.ExpertAccord = true;
        await vm.PoserVentilateurExpertCommand.ExecuteAsync(null);
        appels.Should().Be(0);
    }
}
