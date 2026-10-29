using DrDxt.Tests.Shared;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Reprendre l'essai : parties hors plan, autre processus, mémoire d'essai illisible, réglage changé depuis.</summary>
public sealed partial class EssaiEnAlternanceTests
{
    // Le relevé de fond (App.ReleverEnFond) : mêmes fichiers, aucun levier, aucun helper. Il ne pose ni ne rend rien.
    private ScanViewModel ReleveDeFond(ScanViewModel app)
    {
        var fond = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        fond.MemoireParties = app.MemoireParties;
        fond.MemoireEssai = app.MemoireEssai;
        fond.PrendreVerrouEssai = app.PrendreVerrouEssai;
        fond.Horloge = () => _maintenant;
        fond.DemarrageDuPc = () => _demarrage;
        return fond;
    }

    // Le PC sort de veille le matin (Kernel-Boot 27 au réveil, un levier volatil est rendu), puis une partie est relevée.
    private async Task JouerApresUnReveil(ScanViewModel releveur)
    {
        _demarrage = _maintenant.AddDays(1).AddMinutes(-3);
        _maintenant = _maintenant.AddDays(1);
        releveur.EnregistrerPartie(new PartieRelevee("JeuBClient-Win64-Shipping.exe", _maintenant, _maintenant.AddMinutes(30), 4,
            130, 80, BottleneckState.CpuBoundOrAppBound));
        await releveur.DerniereAvanceeEssai;
    }

    // Un réveil rend la limite et le relevé de fond ne la repose pas : les parties suivantes, jouées « sans » là où les
    // blocs les voulaient « avec », comptaient dans les blocs. Jouées hors du plan, elles
    // ne comptent plus, et l'App ouverte dit quoi faire, avec le nom du jeu.
    [Fact]
    public async Task Une_partie_jouee_hors_du_plan_ne_compte_pas_et_l_App_dit_quoi_faire()
    {
        var app = Vm();
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        var fond = ReleveDeFond(app);

        for (var jour = 0; jour < 4; jour++) await JouerApresUnReveil(fond);

        app.MemoireParties!.Lire().Should().HaveCount(4).And.OnlyContain(p => p.Essai == null, "aucune n'avait le levier que les blocs voulaient");
        _poses.Should().Be(1, "témoin : rien ne s'est posé sans fenêtre");
        app.ChargerParties();
        app.EssaiEnAttente.Should().BeTrue();
        app.LigneEssai.Should().Be("Essai.EnAttente");
    }

    // « Reprendre l'essai » est un clic : il pose le levier (l'invite Windows éventuelle vient de lui), et la partie suivante
    // compte avec.
    [Fact]
    public async Task Reprendre_l_essai_pose_le_levier_et_la_partie_suivante_compte()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.ChargerParties();
        app.EssaiEnAttente.Should().BeTrue("témoin");

        await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(2, "le clic pose, même sans helper déjà lancé : c'est lui qui porte l'invite");
        app.EssaiEnAttente.Should().BeFalse();
        await Jouer(app, 90);
        app.MemoireParties!.Lire().OrderBy(p => p.Debut).Last().Essai.Should().Be(AlternanceDesEssais.Marque(Id, true));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void La_ligne_d_attente_nomme_le_jeu_et_le_geste(string langue)
    {
        var dire = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = new System.Globalization.CultureInfo(langue);
        var ligne = string.Format(culture, dire.Get("Essai.EnAttente", culture), "Limite de puissance", "JeuB");

        ligne.Should().Contain("JeuB").And.Contain("DrDXT").And.Contain(dire.Get("Essai.Reprendre", culture));
        dire.Get("Essai.TonJeu", culture).Should().NotBe("Essai.TonJeu");
    }

    // L'App et le relevé de fond tournent ensemble et écrivent le même essai. Pendant qu'un processus tient le verrou de
    // l'essai (il pose ou rend), l'autre n'écrit rien et ne marque pas la partie : écrite par-dessus, la pose était perdue
    // (fichier « rendu », levier posé, et « Arrêter l'essai » ne rendait plus rien).
    [Fact]
    public async Task Pendant_que_l_autre_processus_tient_l_essai_le_releve_de_fond_n_ecrit_ni_ne_marque_rien()
    {
        var app = Vm();
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        var fond = ReleveDeFond(app);
        var fichier = Path.Combine(_dossier, "essai.json");
        var avant = File.ReadAllText(fichier);

        using (VerrouDuReleve.Prendre(CheminDuVerrou)) await JouerApresUnReveil(fond);

        File.ReadAllText(fichier).Should().Be(avant);
        app.MemoireParties!.Lire().Should().ContainSingle().Which.Essai.Should().BeNull();
        await JouerApresUnReveil(fond);
        File.ReadAllText(fichier).Should().NotBe(avant, "témoin : verrou libre, le réveil se note");
    }

    [Fact]
    public async Task Reprendre_pendant_que_l_autre_processus_tient_l_essai_ne_pose_rien_et_le_dit()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.AttenteDuVerrouEssai = TimeSpan.Zero;

        using (VerrouDuReleve.Prendre(CheminDuVerrou)) await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(1, "rien ne se pose pendant que l'autre processus change l'essai");
        app.LigneEssai.Should().Be("Essai.Occupe");
    }

    // Rien ne se pose pendant une partie : le clic le dit au lieu de ne rien faire.
    [Fact]
    public async Task Reprendre_pendant_une_partie_dit_de_fermer_le_jeu()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.JeuOuvertPourLeTest = () => true;

        await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(1);
        app.LigneEssai.Should().Be("Essai.FermeLeJeu");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Les_lignes_du_clic_qui_attend_disent_quoi_faire(string langue)
    {
        var dire = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = new System.Globalization.CultureInfo(langue);

        string.Format(culture, dire.Get("Essai.FermeLeJeu", culture), "Limite de puissance", "JeuB")
            .Should().Contain("JeuB").And.Contain(dire.Get("Essai.Reprendre", culture));
        string.Format(culture, dire.Get("Essai.Occupe", culture), "Limite de puissance")
            .Should().Contain(dire.Get("Essai.Reprendre", culture));
    }

    // La mémoire de l'essai ne se lit pas (tenue par un autre programme) : ce n'est pas « aucun essai ». Un essai neuf
    // écraserait l'origine de celui en cours ; il ne part pas, et rien n'est posé.
    [Fact]
    public async Task Une_memoire_d_essai_illisible_n_est_pas_aucun_essai()
    {
        var vm = Vm();
        var chemin = Path.Combine(_dossier, "essai.json");
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(Id, true, false, _maintenant)).IsOk.Should().BeTrue();

        using (new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.None))
            (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().Contain("Essai.DejaEnCours", "le clic ne reste pas muet");
        _poses.Should().Be(0, "rien n'est posé sur un essai qu'on ne sait pas lire");
        _rendus.Should().Be(0);
        vm.MemoireEssai.Lire()!.PoseALOrigine.Should().BeTrue("l'origine de l'essai en cours reste");
    }

    // Abîmée, la mémoire de l'essai est mise de côté avant qu'un essai neuf la remplace : l'origine perdue s'y lit encore.
    [Fact]
    public async Task Une_memoire_d_essai_abimee_est_mise_de_cote_avant_un_essai_neuf()
    {
        var vm = Vm();
        File.WriteAllText(Path.Combine(_dossier, "essai.json"), "{\"LevierId\":\"gpu.power_limit\",\"PoseALOrig");

        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();

        Directory.GetFiles(_dossier, "essai.json.mis-de-cote-*").Should().ContainSingle()
            .Which.Should().Match(f => File.ReadAllText(f).Contains("PoseALOrig"));
    }

    private const string Reflex = "latency.reflex";

    /// <summary>Reflex posé par l'essai (0 → 1), puis passé à 2 par le joueur hors de DrDXT.</summary>
    private async Task<ScanViewModel> EssaiPuisChangeParLeJoueur()
    {
        var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")
            .Returns(Result<uint?>.Ok(1u));
        var vm = Vm(id: Reflex, origine: InversesDeTest.Pour(Reflex, "0"), posee: InversesDeTest.Pour(Reflex, "1"), registre: registre);
        (await vm.DemarrerEssaiAsync(Reflex)).Should().BeTrue();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")
            .Returns(Result<uint?>.Ok(2u));
        return vm;
    }

    // La bascule « sans » rendrait la valeur d'avant par-dessus un réglage que le joueur a changé depuis. Rien n'est réécrit :
    // l'essai se met en pause, et c'est dit.
    [Fact]
    public async Task Une_bascule_n_ecrase_pas_un_reglage_change_depuis_par_le_joueur()
    {
        var vm = await EssaiPuisChangeParLeJoueur();

        await Jouer(vm, 90);

        _rendus.Should().Be(0);
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        vm.LigneEssai.Should().Contain("Essai.ChangeDepuis");
    }

    // L'arrêt non plus : l'essai s'arrête, la valeur du joueur reste, et la ligne ne dit pas « l'état d'avant est revenu ».
    [Fact]
    public async Task L_arret_n_ecrase_pas_un_reglage_change_depuis_par_le_joueur()
    {
        var vm = await EssaiPuisChangeParLeJoueur();

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(0);
        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Contain("Essai.ArreteChangeDepuis");
    }

    // Le levier refuse la pose : l'essai ne part pas, et la ligne le dit au lieu de laisser le clic sans réponse.
    [Fact]
    public async Task Une_pose_refusee_au_lancement_de_l_essai_se_dit()
    {
        var vm = Vm();
        _levier.ApplyAsync().Returns(Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.HelperOpFailed, "refus")));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().StartWith("Essai.NonPose");
        vm.MemoireEssai!.Lire().Should().BeNull();
    }
}
