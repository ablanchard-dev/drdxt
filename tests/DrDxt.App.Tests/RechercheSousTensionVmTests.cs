using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La recherche de sous-tension pilotée par l'App. Le helper, le test de charge, les erreurs matérielles et l'horloge de démarrage
// sont injectés : rien ne touche la machine. Couvert : l'origine rendue par 0 (relatif), le retour vérifié, l'interruption jamais
// lue comme un plantage, l'arrêt de sécurité instable, les erreurs non lues jamais « aucune », pas de pose sans référence, le
// refus reconnu à son code d'erreur, l'exclusion avec le test de refroidissement.
public sealed class RechercheSousTensionVmTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-soustension-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private sealed class Charge : IDisposable { public void Dispose() { } }

    private readonly List<int> _poses = [];
    private int _decalage;
    private int _testsDeCharge;

    private ScanViewModel Vm(Func<int, Result>? poser = null, Func<int, int?>? whea = null, bool jeuOuvert = false,
        Func<int, int?>? chaleur = null)
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k switch
            {
                "SousTension.Verdict" => "{0} mV tient {5} min : {1} contre {2} °C, freiné {3} contre {4} % ; au BIOS {6} mV",
                "SousTension.VerdictUnPalier" => "{0} mV seul tient {5} min : {1} contre {2} °C, freiné {3} contre {4} % ; au BIOS {6} mV",
                "SousTension.Aucune" => "aucun palier : {0}",
                "SousTension.Refusee" => "UnderVolt Protection",
                "SousTension.Raison.Erreurs" => "{0} erreur(s) matérielle(s)",
                "SousTension.Raison.Plantage" => "plantage",
                "SousTension.Raison.ArretSecurite" => "arrêt de sécurité",
                "SousTension.Raison.Interrompue" => "interrompue",
                "SousTension.Raison.ErreursNonLues" => "erreurs non lues",
                "SousTension.Raison.NonLu" => "non lu",
                "SousTension.Raison.TropChaudAuDepart" => "trop chaud au départ",
                "SousTension.NonRendue" => " NON RENDUE : redémarre",
                "SousTension.Rendue" => " ORIGINE RENDUE",
                "SousTension.NonDisponible" => "NON DISPONIBLE",
                _ => k,
            });
        vm.MemoireSousTension = new MemoireSousTension(Path.Combine(_dossier, "sous-tension.json"));
        vm.PoserSousTension = mv =>
        {
            _poses.Add(mv);
            var r = poser?.Invoke(mv) ?? Result.Ok();
            if (r.IsOk) _decalage = mv;
            return Task.FromResult(r);
        };
        // La chaleur baisse avec la sous-tension : 96 °C à 0 mV, 2 °C de moins par 10 mV.
        vm.FabriqueTestRefroidissement = () => { _testsDeCharge++; return new TestDeRefroidissement(_ => new Charge(),
            () => (chaleur ?? (mv => 96 + mv / 5))(_decalage) is { } c ? new EtatCpu(c, 80, c >= 95, false, false, false) : null,
            (_, ct) => Task.Delay(1, ct)); };
        vm.DureeDuTestRefroidissement = TimeSpan.FromSeconds(5);
        vm.JeuOuvertPourLeTest = () => jeuOuvert;
        vm.ErreursWheaDepuis = _ => whea is null ? 0 : whea(_decalage);
        return vm;
    }

    [Fact]
    public async Task Tous_les_paliers_tiennent_le_verdict_est_le_plus_bas_et_l_origine_est_rendue()
    {
        var vm = Vm();
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        _poses.Should().Equal(-30, 0, -30, -50, -70, -90, 0);
        vm.ResultatSousTension.Should().Contain("-90 mV").And.Contain("BIOS").And.NotContain("NON RENDUE");
        vm.SousTensionTrouvee.Should().BeTrue();
    }

    // L'essai d'écriture du début est noté avant de partir : un plantage à cet instant se rattache au palier, et la tension
    // d'origine se rend au retour. La marque tombe dès que l'origine est rendue.
    [Fact]
    public async Task L_essai_d_ecriture_est_note_avant_de_partir()
    {
        var notes = new List<DateTimeOffset?>();
        ScanViewModel? vm = null;
        vm = Vm(poser: mv => { notes.Add(vm!.MemoireSousTension!.Lire()?.PalierCommenceLe); return Result.Ok(); });

        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        notes[0].Should().NotBeNull("l'essai à -30 mV est noté avant d'écrire");
        notes[1].Should().NotBeNull("le retour à 0 mV de l'essai part encore sous la marque");
    }

    [Fact]
    public async Task Une_erreur_materielle_arrete_et_garde_le_dernier_palier_tenu()
    {
        var vm = Vm(whea: mv => mv <= -50 ? 2 : 0);
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().Equal(-30, 0, -30, -50, 0);
        vm.ResultatSousTension.Should().Contain("-30 mV").And.Contain("2 erreur(s) matérielle(s)");
    }

    [Fact]
    public async Task Le_retour_a_l_origine_qui_echoue_est_dit_avec_le_geste()
    {
        var vm = Vm(poser: mv => mv == 0 ? Result.Fail(ErrorCodes.HelperOpFailed, "tube") : Result.Ok());
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        vm.ResultatSousTension.Should().Contain("NON RENDUE : redémarre");
    }

    // Deux minutes de charge ne prouvent pas une sous-tension en jeu : le texte dit ce qui a été mesuré, et le conseil BIOS
    // garde un palier de marge, le tenu juste au-dessus du plus profond.
    [Fact]
    public async Task Le_verdict_dit_la_duree_mesuree_et_le_BIOS_garde_un_palier_de_marge()
    {
        var vm = Vm();
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        vm.ResultatSousTension.Should().StartWith("-90 mV tient 0.08 min").And.Contain("au BIOS -70 mV");
    }

    // Un seul palier a tenu : il n'y a pas de palier tenu au-dessus de lui, le conseil reste ce palier, et le texte ne parle
    // d'aucune marge.
    [Fact]
    public async Task Un_seul_palier_tenu_reste_le_conseil_du_BIOS_sans_parler_de_marge()
    {
        var vm = Vm(whea: mv => mv <= -50 ? 2 : 0);
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        vm.ResultatSousTension.Should().StartWith("-30 mV seul tient").And.Contain("au BIOS -30 mV");
    }

    // Le helper refuse avant tout palier (tension illisible, module absent) : rien n'a été posé, et son message anglais ne
    // se colle jamais dans la phrase.
    [Fact]
    public async Task Un_refus_avant_tout_palier_dit_non_disponible_sans_le_message_du_helper()
    {
        var vm = Vm(poser: _ => Result.Fail(ErrorCodes.HelperOpFailed, "CPU voltage could not be read"));
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _testsDeCharge.Should().Be(0);
        vm.SousTensionTrouvee.Should().BeFalse();
        vm.ResultatSousTension.Should().Be("NON DISPONIBLE");
    }

    [Fact]
    public void Non_disponible_ne_parle_d_aucun_retour_dans_les_deux_langues()
    {
        var textes = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        foreach (var (culture, rendue) in new[] { ("fr", "rendu"), ("en", "restor") })
            textes.Get("SousTension.NonDisponible", new System.Globalization.CultureInfo(culture))
                .Should().NotBe("SousTension.NonDisponible", culture).And.NotContain(rendue, culture);
    }

    // La carte suit la condition du helper (Intel et PawnIO, lus sur la machine), pas le fabricant lu au scan.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void La_recherche_ne_se_propose_que_la_ou_le_helper_peut_poser(bool possible)
    {
        var vm = Vm();
        vm.SousTensionPossibleSurLaMachine = () => possible;
        vm.SousTensionPossible.Should().Be(possible);
    }

    [Fact]
    public void Le_verdict_a_un_seul_palier_ne_promet_aucune_marge_dans_les_deux_langues()
    {
        var textes = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        foreach (var (culture, marge) in new[] { ("fr", "marge"), ("en", "margin") })
        {
            var langue = new System.Globalization.CultureInfo(culture);
            textes.Get("SousTension.Verdict", langue).Should().Contain(marge, culture);
            textes.Get("SousTension.VerdictUnPalier", langue).Should().NotBe("SousTension.VerdictUnPalier", culture).And.NotContain(marge, culture);
        }
    }

    // « Rendue » ne se dit que si le retour a réussi : les deux phrases à la suite se contredisaient.
    [Fact]
    public async Task L_origine_n_est_dite_rendue_que_si_le_retour_a_reussi()
    {
        var reussi = Vm();
        await reussi.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        reussi.ResultatSousTension.Should().Contain("ORIGINE RENDUE").And.NotContain("NON RENDUE");

        _poses.Clear();
        var rate = Vm(poser: mv => mv == 0 ? Result.Fail(ErrorCodes.HelperOpFailed, "tube") : Result.Ok());
        await rate.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        rate.ResultatSousTension.Should().Contain("NON RENDUE").And.NotContain("ORIGINE RENDUE");
    }

    [Fact]
    public async Task Sans_palier_tenu_l_origine_n_est_dite_rendue_que_si_le_retour_a_reussi()
    {
        var reussi = Vm(whea: _ => 2);
        await reussi.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        reussi.ResultatSousTension.Should().StartWith("aucun palier").And.Contain("ORIGINE RENDUE");

        _poses.Clear();
        var rate = Vm(whea: _ => 2, poser: mv => mv == 0 ? Result.Fail(ErrorCodes.HelperOpFailed, "tube") : Result.Ok());
        await rate.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        rate.ResultatSousTension.Should().Contain("NON RENDUE").And.NotContain("ORIGINE RENDUE");
    }

    [Fact]
    public async Task Un_arret_de_securite_a_99_degres_compte_comme_instable()
    {
        var vm = Vm(chaleur: mv => mv <= -50 ? 100 : 90);
        vm.DureeDuTestRefroidissement = TimeSpan.FromSeconds(15); // l'arrêt de sécurité demande 10 s au seuil
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        vm.ResultatSousTension.Should().Contain("-30 mV").And.Contain("arrêt de sécurité");
    }

    [Fact]
    public async Task Des_erreurs_materielles_non_lues_ne_valent_pas_aucune_erreur()
    {
        var vm = Vm(whea: _ => null);
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        vm.SousTensionTrouvee.Should().BeFalse();
        vm.ResultatSousTension.Should().Contain("erreurs non lues");
        _poses.Should().Equal(-30, 0, -30, 0);
    }

    [Fact]
    public async Task Sans_reference_lue_aucun_palier_n_est_essaye()
    {
        var vm = Vm(chaleur: _ => null);
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().Equal([-30, 0], "seul l'essai d'écriture, rendu aussitôt");
        vm.ResultatSousTension.Should().Contain("non lu");
    }

    [Fact]
    public async Task Undervolt_Protection_se_reconnait_a_son_code_et_rien_n_est_a_rendre()
    {
        var vm = Vm(poser: _ => Result.Fail(ErrorCodes.TensionRefusee, "any text"));
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().Equal([-30], "rien n'a été posé : rien à rendre");
        _testsDeCharge.Should().Be(0, "le refus se sait en une écriture, sans deux minutes de charge pour rien");
        vm.SousTensionTrouvee.Should().BeFalse();
        vm.ResultatSousTension.Should().Be("UnderVolt Protection");
    }

    // Le geste au BIOS se dit avec les menus de la marque lue au scan ; une marque sans chemin sourcé garde le texte général.
    [Theory]
    [InlineData("Micro-Star International Co., Ltd.", "SousTension.Refusee.MSI")]
    [InlineData("ASUSTeK COMPUTER INC.", "SousTension.Refusee.ASUS")]
    [InlineData("ASRock", "SousTension.Refusee.ASRock")]
    [InlineData("Gigabyte Technology Co., Ltd.", "UnderVolt Protection")]
    [InlineData(null, "UnderVolt Protection")]
    public async Task Le_refus_donne_le_chemin_de_la_marque_de_la_carte(string? fabricant, string attendu)
    {
        var vm = Vm(poser: _ => Result.Fail(ErrorCodes.TensionRefusee, "any text"));
        vm.FabricantDeCarte = () => fabricant;
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        vm.ResultatSousTension.Should().Be(attendu);
    }

    [Fact]
    public async Task Deja_a_99_degres_a_la_tension_d_origine_c_est_dit_et_aucun_palier_n_est_essaye()
    {
        var vm = Vm(chaleur: _ => 100);
        vm.DureeDuTestRefroidissement = TimeSpan.FromSeconds(15); // l'arrêt de sécurité demande 10 s au seuil
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().Equal(-30, 0);
        vm.ResultatSousTension.Should().Contain("trop chaud au départ").And.NotContain("non lu");
    }

    [Fact]
    public async Task Arreter_en_plein_palier_ne_compte_rien_comme_tenu_rend_l_origine_et_ne_fera_pas_croire_a_un_plantage()
    {
        ScanViewModel? vm = null;
        var lectures = 0;
        vm = Vm(chaleur: mv => { if (mv == -50 && ++lectures == 2) vm!.ArreterRechercheSousTensionCommand.Execute(null); return 90; });
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        _poses.Should().Equal(-30, 0, -30, -50, 0);
        vm.ResultatSousTension.Should().Contain("interrompue");
        var memoire = vm.MemoireSousTension!.Lire()!;
        memoire.Finie.Should().BeTrue();
        memoire.Stables.Should().ContainSingle("seul -30 a tenu ; -50 coupé ne compte pas");
        memoire.ApresRedemarrage(DateTimeOffset.MaxValue).Should().Be(memoire, "interrompue : jamais lue comme un plantage");
    }

    [Fact]
    public async Task Pendant_le_test_de_refroidissement_la_recherche_ne_part_pas()
    {
        var vm = Vm();
        vm.TestRefroidissementEnCours = true;
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().BeEmpty();
    }

    [Fact]
    public async Task Jeu_ouvert_rien_ne_part()
    {
        var vm = Vm(jeuOuvert: true);
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().BeEmpty();
        vm.ResultatSousTension.Should().Be("Refroidissement.Test.JeuOuvert");
    }

    [Fact]
    public async Task Un_plantage_en_plein_palier_est_lu_au_demarrage_suivant()
    {
        var debut = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var memoire = new MemoireSousTension(Path.Combine(_dossier, "sous-tension.json"));
        memoire.Ecrire(RechercheSousTension.Nouvelle(debut).Apres(IssuePalier.Stable(92, 20)) with { PalierCommenceLe = debut.AddMinutes(3) });
        var vm = Vm();
        vm.DemarrageDuPc = () => debut.AddMinutes(5);

        await vm.ReprendreRechercheSousTensionAsync();

        vm.ResultatSousTension.Should().Contain("-30 mV").And.Contain("plantage");
        memoire.Lire()!.Finie.Should().BeTrue();
    }

    // L'App tuée en plein palier (gestionnaire des tâches), sans redémarrage : ni finally ni fermeture n'ont
    // tourné, le palier est encore posé. Au relancement, l'origine est rendue (vérifiée) et la recherche notée interrompue.
    [Fact]
    public async Task L_App_tuee_en_plein_palier_rend_l_origine_au_relancement()
    {
        var demarrage = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var memoire = new MemoireSousTension(Path.Combine(_dossier, "sous-tension.json"));
        memoire.Ecrire(RechercheSousTension.Nouvelle(demarrage.AddHours(1)).Apres(IssuePalier.Stable(92, 20))
            with { PalierCommenceLe = demarrage.AddHours(1).AddMinutes(3) });
        var vm = Vm();
        vm.DemarrageDuPc = () => demarrage;

        await vm.ReprendreRechercheSousTensionAsync();

        _poses.Should().Equal([0], "l'origine rendue");
        vm.ResultatSousTension.Should().Contain("interrompue");
        memoire.Lire()!.Finie.Should().BeTrue();
    }

    // Le helper dit « non rendue » (un plan laissé changé après un refus) : l'App tente de rendre
    // l'origine à la fin, au lieu de croire que rien n'a été posé.
    [Fact]
    public async Task Une_tension_non_rendue_par_le_helper_fait_tenter_le_retour_a_la_fin()
    {
        var vm = Vm(poser: mv => mv == -30 ? Result.Fail(ErrorCodes.TensionNonRendue, "laissée") : Result.Ok());
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
        _poses.Should().Equal(-30, 0);
    }

    // La recherche reprend sur le fil d'interface. Bloquer ce fil (OnExit, .Wait) empêchait son finally de rendre
    // l'origine : le processus partait avec le palier posé. La fermeture de la fenêtre rend sans bloquer le fil.
    [Fact]
    public void Fermer_la_fenetre_pendant_un_palier_rend_la_tension_d_origine()
    {
        List<int>? posesALaFermeture = null;
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(d));
            d.InvokeAsync(async () =>
            {
                try
                {
                    var vm = Vm();
                    _ = vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
                    while (!_poses.Contains(-30)) await Task.Delay(1);
                    vm.QuelqueChoseARendre.Should().BeTrue();
                    await vm.RendreAvantFermetureAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    posesALaFermeture = _poses.ToList();
                }
                catch (Exception ex) { erreur = ex; }
                finally { d.InvokeShutdown(); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        fil.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        erreur.Should().BeNull();
        posesALaFermeture!.Last().Should().Be(0, "la tension d'origine est rendue avant la fermeture");
    }

    // OnExit bloque le fil d'interface : son secours rend l'origine directement, sans attendre le finally de la recherche.
    [Fact]
    public void Le_secours_d_OnExit_rend_la_tension_d_origine_meme_en_bloquant_le_fil()
    {
        List<int>? posesALaFermeture = null;
        RechercheSousTension? noteeALaFermeture = null;
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(d));
            d.InvokeAsync(async () =>
            {
                try
                {
                    var vm = Vm();
                    _ = vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);
                    while (!_poses.Contains(-30)) await Task.Delay(1);
                    vm.QuelqueChoseARendre.Should().BeTrue();
                    try { Task.Run(vm.RendreOrigineSansLeFilAsync).Wait(TimeSpan.FromSeconds(5)); } catch { }
                    posesALaFermeture = _poses.ToList();
                    noteeALaFermeture = vm.MemoireSousTension!.Lire();
                }
                catch (Exception ex) { erreur = ex; }
                finally { d.InvokeShutdown(); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        fil.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        erreur.Should().BeNull();
        posesALaFermeture!.Last().Should().Be(0, "OnExit bloque le fil : l'origine est rendue sans attendre le finally");
        // Windows qui redémarre passe par ici sans laisser la recherche finir : le palier ne doit pas se lire « plantage ».
        noteeALaFermeture!.PalierCommenceLe.Should().BeNull();
        noteeALaFermeture.Finie.Should().BeTrue();
    }

    // Un refus du BIOS après des paliers tenus : le verdict garde le dernier palier et dit le refus en mots, pas son code.
    [Fact]
    public async Task Un_refus_apres_des_paliers_tenus_se_dit_en_mots()
    {
        var vm = Vm(poser: mv => mv == -50 ? Result.Fail(ErrorCodes.TensionRefusee, "any text") : Result.Ok());
        await vm.LancerRechercheSousTensionCommand.ExecuteAsync(null);

        vm.ResultatSousTension.Should().Contain("-30 mV").And.Contain("UnderVolt Protection").And.NotContain(ErrorCodes.TensionRefusee);
    }

    // Au lancement, l'origine d'une recherche interrompue se rend sans clic : aucune invite Windows (SansGesteDuJoueur).
    [Fact]
    public async Task L_origine_rendue_au_relancement_se_rend_sans_geste()
    {
        var demarrage = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var memoire = new MemoireSousTension(Path.Combine(_dossier, "sous-tension.json"));
        memoire.Ecrire(RechercheSousTension.Nouvelle(demarrage.AddHours(1)).Apres(IssuePalier.Stable(92, 20))
            with { PalierCommenceLe = demarrage.AddHours(1).AddMinutes(3) });
        var portees = new List<bool>();
        var vm = Vm(poser: _ => { portees.Add(SansGesteDuJoueur.Actif); return Result.Ok(); });
        vm.DemarrageDuPc = () => demarrage;

        await vm.ReprendreRechercheSousTensionAsync();

        portees.Should().NotBeEmpty("témoin : l'origine est rendue").And.OnlyContain(actif => actif);
    }
}
