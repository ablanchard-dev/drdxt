using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// 07/10/2026 (chantier J4) : un levier à chaud essayé une partie sur deux, sans geste à chaque partie. Le vrai
/// ViewModel, un levier factice, de vrais fichiers temporaires : rien ne touche la machine.
/// </summary>
public sealed class EssaiEnAlternanceTests : IDisposable
{
    private const string Id = "gpu.power_limit";
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-essai-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private readonly ITweak _levier = Substitute.For<ITweak>();
    private int _poses, _rendus;
    private DateTimeOffset _maintenant = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _demarrage = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private ScanViewModel Vm(bool helperJoignable = true)
    {
        _levier.Id.Returns(Id);
        _levier.RequiresElevation.Returns(true);
        _levier.ApplyAsync().Returns(_ => { _poses++; return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90"))); });
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { _rendus++; return Task.FromResult(Result.Ok()); });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")));
        vm.MemoireParties = new PartiesMemorisees(Path.Combine(_dossier, "choix.json"), Path.Combine(_dossier, "parties.json"));
        vm.MemoireEssai = new MemoireDeLEssai(Path.Combine(_dossier, "essai.json"));
        vm.LevierParId = id => id == Id ? _levier : null;
        vm.HelperJoignableSansUac = () => Task.FromResult(helperJoignable);
        vm.Horloge = () => _maintenant;
        vm.DemarrageDuPc = () => _demarrage;
        return vm;
    }

    private async Task Jouer(ScanViewModel vm, double low1)
    {
        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, low1,
            BottleneckState.CpuBoundOrAppBound));
        await vm.DerniereAvanceeEssai;
    }

    [Fact]
    public async Task Le_levier_alterne_une_partie_sur_deux_puis_reste_sur_le_meilleur_etat()
    {
        var vm = Vm();
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        _poses.Should().Be(1, "le clic pose le levier : la première partie l'a avec");

        await Jouer(vm, 90);
        _rendus.Should().Be(1, "après une partie avec, la suivante est sans");
        await Jouer(vm, 80);
        _poses.Should().Be(2);
        await Jouer(vm, 91);
        await Jouer(vm, 81);
        await Jouer(vm, 92);
        vm.EssaiEnCours.Should().BeTrue("2 contre 3 : pas encore tranché");
        await Jouer(vm, 79);

        vm.EssaiEnCours.Should().BeFalse("3 contre 3 nets : tranché");
        vm.LigneEssai.Should().Be("Essai.Fini.Mieux");
        vm.MemoireParties!.Lire().Select(p => p.Essai).Should().OnlyContain(e => e != null && e.StartsWith(Id + ":", StringComparison.Ordinal));
        (_poses - _rendus).Should().Be(1, "mieux en jeu : il reste posé");
    }

    // GARDE1 (09/10/2026) : la pose de l'essai se date, comme celles d'Optimiser : la garde d'incident la couvre.
    [Fact]
    public async Task La_pose_de_l_essai_se_date_pour_la_garde()
    {
        var vm = Vm();
        vm.JournalChangements = new JournalDesChangements(Path.Combine(_dossier, "changements.json"));
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        vm.JournalChangements.Lire().Should().Contain(c => c.Cle == "pose:" + Id && c.Quand == _maintenant);
    }

    // 09/10/2026 : DrDXT relancé pendant un essai : sans scan ni fin de partie, l'essai ne se disait pas et son bouton
    // d'arrêt restait dans la zone de résultats, invisible.
    [Fact]
    public async Task Un_essai_en_cours_se_dit_des_le_lancement_et_s_arrete_sans_scan()
    {
        (await Vm().DemarrerEssaiAsync(Id)).Should().BeTrue();

        var relance = Vm();
        var (poses, rendus) = (_poses, _rendus);   // re-régler le substitut rappelle ses anciens rappels
        relance.LigneEssai.Should().BeNull("témoin : rien n'est dit avant le chargement");
        relance.ChargerParties();
        relance.LigneEssai.Should().Be("Essai.EnCours");
        relance.EssaiEnCours.Should().BeTrue();

        await relance.ArreterEssaiCommand.ExecuteAsync(null);
        relance.EssaiEnCours.Should().BeFalse();
        (_rendus - rendus).Should().Be(1, "l'arrêt rend l'état d'origine");
        (_poses - poses).Should().Be(0);
        relance.LigneEssai.Should().Be("Essai.Arrete");
    }

    [Fact]
    public async Task Sans_helper_deja_lance_l_essai_se_met_en_pause_sans_invite_windows()
    {
        var vm = Vm(helperJoignable: false);
        await vm.DemarrerEssaiAsync(Id);

        await Jouer(vm, 90);

        _rendus.Should().Be(0, "rendre le levier aurait demandé une autorisation Windows en jeu");
        vm.LigneEssai.Should().Be("Essai.Pause");
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        await Jouer(vm, 80);
        // Revue F-37 : en pause, rien n'a été touché : l'état (posé) est connu, la partie compte avec lui.
        vm.MemoireParties!.Lire()[0].Essai.Should().Be(AlternanceDesEssais.Marque(Id, true));
    }

    [Fact]
    public async Task Arreter_l_essai_rend_l_etat_d_avant()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(1, "avant l'essai le levier n'était pas posé");
        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.Arrete");
    }

    [Fact]
    public void Seul_un_levier_a_chaud_s_essaie_en_alternance()
    {
        ScanViewModel.PeutAlterner(Id).Should().BeTrue();
        ScanViewModel.PeutAlterner("latency.hagsOn").Should().BeFalse("HAGS demande un redémarrage");
        ScanViewModel.PeutAlterner("inconnu").Should().BeFalse();
    }
    // Revue F-30 : la veille enregistre la partie hors du fil de l'écran ; l'avancée de l'essai (qui rafraîchit des
    // listes liées à l'écran) doit y être postée, comme l'affichage.
    private sealed class FilDeLEcran : SynchronizationContext
    {
        public readonly Queue<(SendOrPostCallback, object?)> Postes = new();
        public override void Post(SendOrPostCallback d, object? state) => Postes.Enqueue((d, state));
        public void Vider() { while (Postes.Count > 0) { var (d, st) = Postes.Dequeue(); d(st); } }
    }

    [Fact]
    public async Task L_avancee_de_l_essai_passe_par_le_fil_de_l_ecran()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        var ui = new FilDeLEcran();
        _maintenant = _maintenant.AddDays(1);

        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 90,
            BottleneckState.CpuBoundOrAppBound), ui);

        _rendus.Should().Be(0, "rien ne bascule hors du fil de l'écran");
        ui.Vider();
        await vm.DerniereAvanceeEssai;
        _rendus.Should().Be(1);
    }

    // Revue F-31 : « Annuler » sur le levier en essai arrête l'essai ; rien ne le repose ensuite.
    [Fact]
    public async Task Annuler_le_levier_en_essai_arrete_l_essai_et_rien_ne_le_repose()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);
        await Jouer(vm, 90);
        await Jouer(vm, 80);

        _poses.Should().Be(1, "seul le clic d'origine l'a posé");
        vm.EssaiEnCours.Should().BeFalse();
    }

    // Revue F-32 : un redémarrage efface un levier de carte ; l'état n'est plus sûr, l'essai s'arrête et le dit.
    [Fact]
    public async Task Un_redemarrage_pendant_l_essai_l_arrete_sans_marquer_la_partie()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull();
        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.Redemarrage");
        (_poses - _rendus).Should().Be(0, "posé au moment du redémarrage, il est rendu : un plan d'alimentation survit au redémarrage");
    }

    // 09/10/2026 (revue) : sans helper joignable, rien ne s'efface : l'essai attend que le joueur clique « Arrêter ».
    [Fact]
    public async Task Apres_un_redemarrage_sans_helper_l_essai_attend_le_clic_qui_rend_l_origine()
    {
        var vm = Vm(helperJoignable: false);
        await vm.DemarrerEssaiAsync(Id);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue();
        vm.LigneEssai.Should().Be("Essai.RedemarrageARendre");
        _rendus.Should().Be(0);

        await vm.ArreterEssaiAsync();
        _rendus.Should().Be(1);
        vm.EssaiEnCours.Should().BeFalse();
    }

    // Revue F-33 : les parties d'un essai précédent du même levier ne tranchent pas le nouvel essai.
    [Fact]
    public async Task Les_parties_d_un_essai_precedent_ne_comptent_pas()
    {
        var vm = Vm();
        foreach (var (low1, pose) in new[] { (90.0, true), (80.0, false), (91.0, true), (81.0, false), (92.0, true), (79.0, false) })
        {
            _maintenant = _maintenant.AddDays(1);
            vm.MemoireParties!.Ajouter(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, low1,
                BottleneckState.CpuBoundOrAppBound, Essai: AlternanceDesEssais.Marque(Id, pose)));
        }
        await vm.DemarrerEssaiAsync(Id);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue("un seul jeu de parties de CET essai : rien de tranché");
    }

    // Revue F-38 : « Annuler » pendant que la fin de partie repose le levier ; l'essai ne revit pas et le levier repart.
    [Fact]
    public async Task Annuler_pendant_la_bascule_n_est_pas_defait_par_elle()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        vm.ArreterEssaiSurAnnulation(Id);
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
        await vm.DerniereAvanceeEssai;

        vm.MemoireEssai!.Lire().Should().BeNull("le joueur a annulé : rien ne relance l'essai");
        _rendus.Should().Be(2, "ce que la fin de partie a posé après l'annulation est rendu");
    }

    // Revue F-38 : « Arrêter l'essai » attend la bascule en cours, puis rend l'état d'origine depuis l'état réel.
    [Fact]
    public async Task Arreter_pendant_la_bascule_rend_l_etat_d_origine()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
        await vm.DerniereAvanceeEssai;
        await arret;

        vm.MemoireEssai!.Lire().Should().BeNull();
        _rendus.Should().Be(2, "posé par la fin de partie, il est rendu par l'arrêt");
        vm.LigneEssai.Should().Be("Essai.Arrete");
    }

    // Revue F-38 : levier déjà posé avant l'essai ; « Arrêter » pendant que la fin de partie le repose ne le retire pas.
    [Fact]
    public async Task Arreter_pendant_la_bascule_garde_un_levier_pose_avant_l_essai()
    {
        var vm = Vm();
        new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json"))
            .Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false, new TweakSnapshot(Id, "100", "90"), null)], null));
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        _rendus.Should().Be(1);
        var enAttente = new List<TaskCompletionSource<Result<TweakSnapshot>>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; var t = new TaskCompletionSource<Result<TweakSnapshot>>(); enAttente.Add(t); return t.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        // La pose de l'arrêt (s'il n'attend pas) finit avant celle de la fin de partie : le pire ordre.
        for (var i = enAttente.Count - 1; i >= 0; i--) enAttente[i].SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
        await vm.DerniereAvanceeEssai;
        foreach (var t in enAttente.Where(t => !t.Task.IsCompleted)) t.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
        await arret;

        _rendus.Should().Be(1, "l'état d'origine est « posé » : rien ne le retire");
        new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")).Lire().Should().Contain(s => s.TweakId == Id);
    }

    // Revue F-39 : un journal qui ne garde rien ne laisse pas un levier posé et invisible.
    [Fact]
    public async Task Un_journal_qui_ne_garde_rien_fait_repartir_le_levier()
    {
        Directory.CreateDirectory(Path.Combine(_dossier, "leviers.json"));
        var vm = Vm();

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();
        _rendus.Should().Be(1);
        vm.MemoireEssai!.Lire().Should().BeNull();
    }

    // Revue F-39 : un journal illisible ne vaut pas « déjà rendu ».
    [Fact]
    public async Task Un_journal_illisible_ne_dit_pas_l_etat_d_avant_revenu()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        File.WriteAllText(Path.Combine(_dossier, "leviers.json"), "{");

        await vm.ArreterEssaiAsync();

        vm.LigneEssai.Should().Be("Essai.ArretRate");
        vm.MemoireEssai!.Lire().Should().NotBeNull();
    }

    // Revue F-46 : une faute pendant l'avancée arrête l'essai au lieu de le mettre en pause sur un état peut-être faux.
    [Fact]
    public async Task Une_faute_pendant_l_avancee_arrete_l_essai()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.HelperJoignableSansUac = () => throw new InvalidOperationException("tuyau");

        await Jouer(vm, 90);

        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Be("Essai.Incertain");
    }
}
