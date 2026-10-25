using System.Globalization;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// `DrDxt.ElevatedHelper.exe --uninstall-revert` : Ce que la desinstallation rend.
//
// Tout en doublure : un manifeste signe par une cle de machine creee dans un dossier temporaire (ACL non
// appliquee), relu par le vrai chemin verify-only du mode ; le registre, le plan, les services et le pilote
// sont une table en memoire ; l'ecriture est un faux client qui enregistre les ops. Rien n'est ecrit sur la
// machine, aucun helper eleve n'est lance.
// -----------------------------------------------------------------------
public sealed class RenduALaDesinstallationTests : IDisposable
{
    private static readonly DateTimeOffset Quand = new(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
    private const string NvTweak = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string Base = "Base Profile";
    private const uint Vsync = 0x00A879CF, Frl = 0x10835002, VsyncForceOn = 0x47814940, VsyncPassive = 0x60925292;
    private static readonly Guid Equilibre = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-desinstallation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* dossier temporaire */ }
    }

    // Les droits corrigés d'un service sont une correction de sécurité : la désinstallation les laisse, et son rapport le dit,
    // service par service.
    [Fact]
    public void Les_droits_corriges_restent_et_le_rapport_nomme_leurs_services()
    {
        var origines = DrDxt.ElevatedHelper.Execution.DroitsDesServices.Racine(_racine);
        Directory.CreateDirectory(origines);
        File.WriteAllText(Path.Combine(origines, "service-ServiceDuJeuA.json"), "{}");
        File.WriteAllText(Path.Combine(origines, "service-ServiceDuJeuB.json"), "{}");

        using (Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR")))
            ModeDesinstallation.DroitsQuiRestent(_racine).Should().Contain("ServiceDuJeuA, ServiceDuJeuB").And.Contain("restent");
        ModeDesinstallation.DroitsQuiRestent(Path.Combine(_racine, "vide")).Should().BeEmpty();
    }

    private static string Op(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;
    private static string Composite(params PrivilegedOp[] ops) => CompositeSnapshotCodec.Encode(ops).Value!;
    private static string Dec(uint v) => v.ToString(CultureInfo.InvariantCulture);

    private static SetRegistryValueOp Reflex(uint v)
        => new(RegistryHiveKind.LocalMachine, NvTweak, "LowLatencyMode", RegistryValueType.DWord, Dec(v));

    private static ApplyNvApiDrsSettingOp Drs(uint id, uint v) => new(id, v, Base);

    private static string Cle(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp s => $"{s.Hive}|{s.Path}|{s.Name}",
        DeleteRegistryValueOp d => $"{d.Hive}|{d.Path}|{d.Name}",
        SetActivePowerPlanOp => "plan",
        SetProcessorBoostModeOp b => "boost|" + b.PlanGuid,
        SetCpuPackagePowerLimitOp => "pl1",
        SetWirelessPowerSavingOp w => "wifi|" + w.PlanGuid,
        RunServiceControlOp r => "service|" + r.ServiceName,
        ApplyNvApiDrsSettingOp a => "drs|" + a.SettingId,
        ApplyNvApiPowerLimitOp n => "nvpuissance|" + n.GpuIndex,
        ApplyAdlxTuningOp a => "adlx|" + a.Kind,
        _ => "?",
    };

    private static string Registre(RegistryHiveKind ruche, string chemin, string nom) => $"{ruche}|{chemin}|{nom}";

    private async Task Signer(DesiredState etat)
        => (await new MachineManifestCommit(_racine, appliquerAcl: false, new DrDxt.Reconciler.SystemClock())
            .ExecuterAsync(etat, repartir: false)).IsOk.Should().BeTrue();

    private static DesiredState Gardes(params AppliedTweak[] t) => new(Array.Empty<AppliedTweak>(), t);

    private Task<string> Rendre(ILectureDesCibles lecture, EcritureFactice ecriture)
    {
        // Une écriture acceptée se voit à la relecture, comme sur la vraie machine (sauf pilote qui ment : SansEffet).
        if (lecture is LectureFactice machine && !ecriture.SansEffet) ecriture.Machine ??= machine;
        return ModeDesinstallation.ExecuterAsync(_racine, appliquerAcl: false, lecture, ecriture, Quand, CancellationToken.None);
    }

    // ── Les réglages de la carte gardés par le joueur ──────────────────────────────

    [Fact]
    public async Task Anti_Lag_AMD_garde_et_encore_pose_est_rendu_change_depuis_il_est_respecte()
    {
        var pose = new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1);
        var origine = new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 0);
        await Signer(Gardes(new AppliedTweak("latency.render_queue_cap", Op(pose), Op(origine), Quand)));

        var ecriture = new EcritureFactice();
        (await Rendre(new LectureFactice { [Cle(pose)] = "1" }, ecriture)).Should().Contain("[Rendu] latency.render_queue_cap");
        ecriture.Recues.Should().ContainSingle().Which.Should().Be(origine);

        // La première désinstallation a mis le manifeste de côté : on resigne pour le second cas.
        await Signer(Gardes(new AppliedTweak("latency.render_queue_cap", Op(pose), Op(origine), Quand)));
        var apres = new EcritureFactice();
        (await Rendre(new LectureFactice { [Cle(pose)] = "0" }, apres)).Should().Contain("[Respecte] latency.render_queue_cap");
        apres.Recues.Should().BeEmpty("le joueur l'a changé depuis : DrDXT n'y touche pas");
    }

    [Fact]
    public async Task La_Vsync_et_FRTC_AMD_gardes_sont_rendus_feuille_par_feuille()
    {
        var vsync = new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 3);
        var frtc = new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, 141);
        var vsyncOrigine = new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1);
        var frtcOrigine = new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, -60);
        await Signer(Gardes(new AppliedTweak("latency.vrr_vsync_cap", Composite(vsync, frtc), Composite(frtcOrigine, vsyncOrigine), Quand)));
        var ecriture = new EcritureFactice();

        await Rendre(new LectureFactice { [Cle(vsync)] = "3", [Cle(frtc)] = "141" }, ecriture);

        ecriture.Recues.Should().Equal(frtcOrigine, vsyncOrigine);
    }

    // Le fichier d'échange rendu à Windows depuis un fichier d'échange désactivé : son origine (PagingFiles absente ou vide)
    // le couperait. Quel que soit le chemin qui l'a inscrite au manifeste (le mapper, un keep de l'autopilote, un manifeste
    // déjà écrit), la désinstallation ne l'écrit jamais, et le dit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Une_origine_qui_coupe_le_fichier_d_echange_n_est_jamais_ecrite_au_rendu(bool supprimee)
    {
        const string Memoire = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        var pose = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Memoire, "PagingFiles", RegistryValueType.MultiString, @"?:\pagefile.sys");
        PrivilegedOp origine = supprimee
            ? new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, Memoire, "PagingFiles")
            : new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Memoire, "PagingFiles", RegistryValueType.MultiString, "");
        await Signer(Gardes(new AppliedTweak("fixit.sane_pagefile", Op(pose), Op(origine), Quand)));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { [Cle(pose)] = @"?:\pagefile.sys" }, ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("fixit.sane_pagefile").And.Contain("fichier d'échange");
    }

    // Même si un manifeste porte une origine de fréquence, la désinstallation ne l'écrit jamais.
    [Fact]
    public async Task Une_origine_de_frequence_n_est_jamais_ecrite_au_rendu()
    {
        // Une sous-tension AMD que la lecture saurait relire : sans la règle écrite au rendu, elle serait remise.
        var pose = new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2500);
        await Signer(Gardes(new AppliedTweak("gpu.undervolt", Op(pose), Op(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2700)), Quand)));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { [Cle(pose)] = "2500" }, ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("jamais écrits");
    }

    [Fact]
    public async Task La_limite_de_puissance_NVIDIA_gardee_est_rendue()
    {
        var pose = new ApplyNvApiPowerLimitOp(0, 90);
        await Signer(Gardes(new AppliedTweak("gpu.power_limit", Op(pose), Op(new ApplyNvApiPowerLimitOp(0, 100)), Quand)));
        var ecriture = new EcritureFactice();

        await Rendre(new LectureFactice { [Cle(pose)] = "90" }, ecriture);

        ecriture.Recues.Should().ContainSingle().Which.Should().Be(new ApplyNvApiPowerLimitOp(0, 100));
    }

    // ── la regle : on ne rend que ce que DrDXT a pose et qui est encore la ─────────────────────

    [Fact]
    public async Task Un_reglage_encore_pose_par_DrDXT_est_rendu()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = new LectureFactice { [Cle(Reflex(2))] = "2" };
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().ContainSingle().Which.Should().Be(Reflex(1));
        rapport.Should().Contain("[Rendu] latency.reflex").And.Contain("lu et vérifié");
    }

    // Le boost rendu (2) puis gardé en jeu revient coupé (0) à la désinstallation, seulement s'il vaut
    // encore ce que DrDXT a posé.
    [Theory]
    [InlineData("2", true)]
    [InlineData("1", false)]
    public async Task Le_mode_de_boost_garde_est_rendu_seulement_s_il_est_encore_celui_de_DrDXT(string lu, bool rendu)
    {
        var pose = new SetProcessorBoostModeOp(Equilibre, 2);
        await Signer(Gardes(new AppliedTweak("fixit.mode_boost", Op(pose), Op(new SetProcessorBoostModeOp(Equilibre, 0)), Quand)));
        var ecriture = new EcritureFactice();

        await Rendre(new LectureFactice { [Cle(pose)] = lu }, ecriture);

        if (rendu) ecriture.Recues.Should().ContainSingle().Which.Should().Be(new SetProcessorBoostModeOp(Equilibre, 0));
        else ecriture.Recues.Should().BeEmpty("le joueur a changé le boost depuis : ce n'est plus la valeur de DrDXT");
    }

    // « Rendue » ne se dit que relue : un pilote qui accepte l'écriture sans rien changer laisse la valeur posée par DrDXT.
    // L'origine reste à rendre (le manifeste est gardé), et le rapport ne dit pas « rendue ».
    [Fact]
    public async Task Une_ecriture_acceptee_mais_que_la_relecture_ne_confirme_pas_n_est_pas_rendue()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));

        var rapport = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, new EcritureFactice { SansEffet = true });

        rapport.Should().NotContain("[Rendu] latency.reflex").And.Contain("[EchecDEcriture] latency.reflex").And.Contain("pas mis de côté");
        Directory.GetFiles(_racine, "desired-state.manifest").Should().ContainSingle("l'origine reste à rendre");
    }

    [Fact]
    public async Task Une_valeur_a_supprimer_encore_la_a_la_relecture_n_est_pas_rendue()
    {
        var supprimer = new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, NvTweak, "LowLatencyMode");
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(supprimer), Quand)));

        var rapport = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, new EcritureFactice { SansEffet = true });

        rapport.Should().NotContain("[Rendu] latency.reflex").And.Contain("[EchecDEcriture] latency.reflex");
    }

    [Fact]
    public async Task Un_reglage_change_a_la_main_depuis_est_respecte_et_le_rapport_le_dit()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = new LectureFactice { [Cle(Reflex(2))] = "0" };
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty("l'utilisateur a mis 0 depuis : ce n'est plus la valeur de DrDXT");
        rapport.Should().Contain("[Respecte] latency.reflex").And.Contain("valeur courante 0");
    }

    [Fact]
    public async Task Une_correction_au_redemarrage_se_compare_a_sa_valeur_voulue_et_la_plus_ancienne_origine_gagne()
    {
        // La correction (Apply) et un keep plus tardif ecrivent la meme feuille : l'origine du keep a ete
        // capturee apres la correction (2), la vraie est celle de la correction (1).
        await Signer(new DesiredState(
            new[] { new AppliedTweak("latency.reflex.lowLatencyMode", "2", Op(Reflex(1)), Quand) },
            new[] { new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(2)), Quand.AddHours(1)) }));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, ecriture);

        ecriture.Recues.Should().ContainSingle().Which.Should().Be(Reflex(1));
        rapport.Should().Contain("[Rendu] latency.reflex.lowLatencyMode").And.Contain("[DejaRendu] latency.reflex");
    }

    // Deux leviers de plan d'alimentation sur la même cible forment une chaîne : Économie→Équilibré (le plus ancien),
    // puis Équilibré→Hautes perf (le plus récent). À la désinstallation, l'actuel (Hautes perf) est ce que DrDXT a laissé :
    // le joueur n'a rien changé, et c'est la plus ancienne origine (Économie) qui ramène à l'état d'avant DrDXT. Sans ce
    // jugement, le plus ancien se croyait « changé depuis » et la machine restait en Équilibré.
    [Fact]
    public async Task Deux_plans_d_alimentation_enchaines_rendent_la_plus_ancienne_origine()
    {
        var ps = KnownPowerPlans.PowerSaver;
        var bal = KnownPowerPlans.Balanced;
        var hp = KnownPowerPlans.HighPerformance;
        // Le plus ancien (Économie→Équilibré), puis le plus récent (Équilibré→Hautes perf).
        await Signer(Gardes(
            new AppliedTweak("fixit.plan_economie", Op(new SetActivePowerPlanOp(bal)), Op(new SetActivePowerPlanOp(ps)), Quand),
            new AppliedTweak("fixit.power_plan", Op(new SetActivePowerPlanOp(hp)), Op(new SetActivePowerPlanOp(bal)), Quand.AddHours(1))));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { ["plan"] = hp.ToString() }, ecriture);

        ecriture.Recues.Should().ContainSingle().Which.Should().Be(new SetActivePowerPlanOp(ps),
            "l'état d'avant DrDXT est Économie, la plus ancienne origine, pas le plan intermédiaire Équilibré");
        rapport.Should().Contain("[Rendu] fixit.plan_economie").And.Contain("[DejaRendu] fixit.power_plan");
    }

    // Le joueur a choisi un plan lui-même après DrDXT : l'actuel n'est plus ce que DrDXT a laissé, rien n'est rendu.
    [Fact]
    public async Task Un_plan_change_par_le_joueur_apres_la_chaine_est_respecte()
    {
        var ps = KnownPowerPlans.PowerSaver;
        var bal = KnownPowerPlans.Balanced;
        var hp = KnownPowerPlans.HighPerformance;
        await Signer(Gardes(
            new AppliedTweak("fixit.plan_economie", Op(new SetActivePowerPlanOp(bal)), Op(new SetActivePowerPlanOp(ps)), Quand),
            new AppliedTweak("fixit.power_plan", Op(new SetActivePowerPlanOp(hp)), Op(new SetActivePowerPlanOp(bal)), Quand.AddHours(1))));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { ["plan"] = KnownPowerPlans.Ultimate.ToString() }, ecriture);

        ecriture.Recues.Should().BeEmpty("le joueur a mis Ultime depuis : ce n'est plus ce que DrDXT a laissé");
        rapport.Should().Contain("[Respecte]");
    }

    // ── les composites : Tout-ou-rien ────────────────────────────────
    //
    // Un levier composite n'a de sens qu'entier. Si une seule de ses feuilles a ete changee a la main,
    // rendre les autres laisserait la machine dans un melange que personne n'a choisi. On ne touche
    // donc a aucune feuille, et le rapport nomme la coupable.

    [Fact]
    public async Task Un_composite_dont_une_feuille_a_ete_changee_a_la_main_n_est_PAS_rendu_du_tout()
    {
        // Le vrai levier capture et ecrit : Allow valait 0, Enabled n'existait pas.
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode").Returns(Result<uint?>.Ok(0u));
        registre.ReadDword(RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled").Returns(Result<uint?>.Ok(null));
        var ecritDuLevier = Substitute.For<IElevatedClient>();
        ecritDuLevier.ExecuteAsync(default!, default).ReturnsForAnyArgs(Result.Ok());
        var snap = (await new GameModeOnTweak(registre, ecritDuLevier).ApplyAsync()).Value!;
        snap.AppliedValueJson.Should().NotBeNullOrEmpty("le levier dit maintenant ce qu'il pose");

        // Comme TweakCandidate.Garder l'inscrit : valeur posee = AppliedValueJson.
        await Signer(Gardes(new AppliedTweak("latency.gameModeOn", snap.AppliedValueJson!, snap.OriginalValueJson, Quand)));
        var lecture = new LectureFactice
        {
            [Registre(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode")] = "1",   // encore a nous
            [Registre(RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled")] = "0", // change a la main
        };
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty("une seule feuille changee a la main gele TOUT le composite");
        rapport.Should().Contain("[CompositePartiellementModifie] latency.gameModeOn")
            .And.Contain("composite partiellement modifié, laissé intact")
            .And.Contain("AutoGameModeEnabled", "le rapport nomme la feuille coupable");
        rapport.Should().NotContain("[Rendu] latency.gameModeOn");
    }

    [Fact]
    public async Task Le_composite_du_pilote_Vsync_et_plafond_est_rendu_quand_les_deux_valeurs_sont_encore_la()
    {
        await Signer(Gardes(new AppliedTweak("latency.vrr_vsync_cap",
            Composite(Drs(Vsync, VsyncForceOn), Drs(Frl, 141)),
            Composite(Drs(Frl, 0), Drs(Vsync, VsyncPassive)), Quand)));
        var lecture = new LectureFactice { ["drs|" + Vsync] = Dec(VsyncForceOn), ["drs|" + Frl] = "141" };
        var ecriture = new EcritureFactice();

        await Rendre(lecture, ecriture);

        ecriture.Recues.Should().Equal(Drs(Frl, 0), Drs(Vsync, VsyncPassive));
    }

    [Fact]
    public async Task Un_composite_X3D_inscrit_AVANT_l_origine_complete_n_est_pas_rendu_a_moitie()
    {
        // Un manifeste d'une ancienne version : seule la valeur voulue du SERVICE est inscrite ; CppcEnable et
        // Game Bar ont une origine mais aucune valeur posee (`PoseesJson` absent). Il en existe encore sur des
        // machines deja equipees : il ne doit rien rendre a moitie. Rendre le service seul laisserait CppcEnable
        // et Game Bar tels que DrDXT les a mis, la moitie d'un levier : le tout-ou-rien refuse l'entier, et le
        // dit. Un manifeste recent inscrit l'origine COMPLETE : `OrigineCompleteDesCompositesTests`.
        await Signer(new DesiredState(new[]
        {
            new AppliedTweak("fixit.x3dVCacheService", "running", Composite(
                new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Processor",
                    "CppcEnable", RegistryValueType.DWord, "0"),
                new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode"),
                new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Stop)), Quand),
        }));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { ["service|AMD3DVCacheSvc"] = "running" }, ecriture);

        ecriture.Recues.Should().BeEmpty("deux feuilles sur trois sont sans valeur posee : on ne rend rien");
        rapport.Split('\n').Count(l => l.Contains("[CompositePartiellementModifie] fixit.x3dVCacheService")).Should().Be(3);
        rapport.Should().Contain("ValeurPoseeInconnue", "le rapport nomme ce qui bloque");
    }

    [Fact]
    public async Task Un_composite_inscrit_sans_valeur_posee_n_est_jamais_rendu_a_l_aveugle()
    {
        // La forme d'avant ce lot : le keep d'un composite portait DesiredValueJson = "".
        await Signer(Gardes(new AppliedTweak("latency.gameModeOn", string.Empty, Composite(
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode", RegistryValueType.DWord, "0"),
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled")), Quand)));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice(), ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Split('\n').Count(l => l.Contains("[CompositePartiellementModifie]")).Should().Be(2);
    }

    [Fact]
    public async Task Un_composite_dont_une_feuille_a_perdu_sa_cible_n_est_pas_rendu_non_plus()
    {
        await Signer(Gardes(new AppliedTweak("latency.vrr_vsync_cap",
            Composite(Drs(Vsync, VsyncForceOn), Drs(Frl, 141)),
            Composite(Drs(Frl, 0), Drs(Vsync, VsyncPassive)), Quand)));
        var lecture = new LectureFactice { ["drs|" + Vsync] = Dec(VsyncForceOn), ["drs|" + Frl] = "141" };
        lecture.Disparues.Add("drs|" + Frl); // le pilote ne connait plus ce reglage.
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("[CompositePartiellementModifie] latency.vrr_vsync_cap")
            .And.Contain("CibleDisparue");
    }

    // Une feuille illisible bloque tout le composite (laissé intact, à juste titre) : ce n'est pas « rendu ». Le manifeste
    // était mis de côté quand même et l'origine perdue ; il reste en place.
    // Une écriture refusée au milieu d'un composite laisse les premières feuilles rendues. À la passe suivante, elles se
    // relisent à leur valeur d'origine : ce n'est pas un changement à la main, le composite se termine.
    [Fact]
    public async Task Un_composite_rendu_a_moitie_par_une_ecriture_refusee_se_termine_a_la_passe_suivante()
    {
        await Signer(Gardes(new AppliedTweak("latency.vrr_vsync_cap",
            Composite(Drs(Vsync, VsyncForceOn), Drs(Frl, 141)),
            Composite(Drs(Frl, 0), Drs(Vsync, VsyncPassive)), Quand)));
        await Rendre(new LectureFactice { ["drs|" + Vsync] = Dec(VsyncForceOn), ["drs|" + Frl] = "141" },
            new EcritureFactice { Echoue = op => op == Drs(Vsync, VsyncPassive) });

        var ecriture = new EcritureFactice();
        var rapport = await Rendre(new LectureFactice { ["drs|" + Vsync] = Dec(VsyncForceOn), ["drs|" + Frl] = "0" }, ecriture);

        ecriture.Recues.Should().Equal(Drs(Vsync, VsyncPassive));
        rapport.Should().Contain("[DejaRendu] latency.vrr_vsync_cap").And.Contain("déjà à sa valeur d'origine")
            .And.NotContain("CompositePartiellementModifie");
        Directory.GetFiles(_racine, "desired-state.manifest").Should().BeEmpty("tout est rendu : il est mis de côté");
    }

    [Fact]
    public async Task Une_feuille_d_origine_absente_et_deja_supprimee_ne_bloque_pas_le_composite()
    {
        var allow = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, GameBar, "AllowAutoGameMode", RegistryValueType.DWord, "1");
        var enabled = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, GameBar, "AutoGameModeEnabled", RegistryValueType.DWord, "1");
        var allowOrigine = allow with { Value = "0" };
        await Signer(Gardes(new AppliedTweak("latency.gameModeOn", Composite(allow, enabled),
            Composite(allowOrigine, new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, GameBar, "AutoGameModeEnabled")), Quand)));

        var ecriture = new EcritureFactice();
        await Rendre(new LectureFactice { [Cle(allow)] = "1", [Cle(enabled)] = null }, ecriture);

        ecriture.Recues.Should().Equal(allowOrigine);
    }

    [Fact]
    public async Task Un_composite_avec_une_feuille_illisible_garde_le_manifeste()
    {
        await Signer(Gardes(new AppliedTweak("latency.vrr_vsync_cap",
            Composite(Drs(Vsync, VsyncForceOn), Drs(Frl, 141)),
            Composite(Drs(Frl, 0), Drs(Vsync, VsyncPassive)), Quand)));
        var lecture = new LectureFactice { ["drs|" + Vsync] = Dec(VsyncForceOn) }; // le plafond ne se relit pas

        var rapport = await Rendre(lecture, new EcritureFactice());

        rapport.Should().Contain("[CompositePartiellementModifie] latency.vrr_vsync_cap").And.Contain("pas mis de côté");
        Directory.GetFiles(_racine, "desired-state.manifest").Should().ContainSingle();
    }

    // ── le manifeste est mis de cote une fois rendu (lot 6e, 11/09/2026) ──────────────────────
    //
    // Il restait en place : une REINSTALLATION y retrouvait des origines capturees avant la
    // desinstallation precedente, deja rendues. Il est renomme, jamais efface.

    [Fact]
    public async Task Une_reinstallation_apres_une_desinstallation_ne_retrouve_aucune_ancienne_origine()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var premier = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, new EcritureFactice());
        premier.Should().Contain("[Rendu] latency.reflex").And.Contain("mis de côté");

        Directory.GetFiles(_racine, "desired-state.manifest").Should().BeEmpty("il a ete renomme");
        Directory.GetFiles(_racine, "desired-state.manifest.rendu-*").Should().ContainSingle(
            "mis de cote, jamais efface : il reste la trace de ce qui a ete rendu");

        // La reinstallation qui suit : le meme mode ne retrouve plus rien a rendre.
        var ecriture = new EcritureFactice();
        var second = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, ecriture);

        ecriture.Recues.Should().BeEmpty("aucune ancienne origine ne doit etre rejouee");
        second.Should().Contain("aucune origine à rendre.");
    }

    // Une origine qui n'a pas pu être rendue (écriture refusée) se perdait : le manifeste était mis de côté quand même, et
    // une réinstallation ne la retrouvait plus. Il reste en place tant qu'une origine n'est pas rendue.
    [Fact]
    public async Task Une_origine_non_rendue_garde_le_manifeste_en_place()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));

        var rapport = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, new EcritureFactice { Echoue = _ => true });

        rapport.Should().Contain("pas mis de côté");
        Directory.GetFiles(_racine, "desired-state.manifest").Should().ContainSingle("l'origine non rendue reste à rendre");
    }

    // Autre compte élevé : sa ruche HKCU n'est pas celle du joueur. Lue, elle concluait « respecté » ou « disparu » sans
    // jamais écrire : aucune ligne non rendue, manifeste mis de côté, origine du joueur perdue. Lecture et écriture HKCU
    // sont refusées ensemble ; la ligne est illisible, le manifeste reste.
    [Fact]
    public async Task Un_autre_compte_garde_le_manifeste_meme_si_sa_ruche_differe()
    {
        var pose = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled", RegistryValueType.DWord, "1");
        var origine = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled", RegistryValueType.DWord, "0");
        await Signer(Gardes(new AppliedTweak("latency.game_mode", Op(pose), Op(origine), Quand)));
        var ruicheDeLAdmin = new LectureFactice { [Cle(pose)] = "0" };

        var rapport = await ModeDesinstallation.ExecuterAsync(_racine, appliquerAcl: false,
            ModeDesinstallation.GarderLaRucheDuJoueur(ruicheDeLAdmin, memeCompte: false),
            ModeDesinstallation.GarderLaRucheDuJoueur(new EcritureFactice(), memeCompte: false), Quand, CancellationToken.None);

        rapport.Should().Contain("pas mis de côté");
        Directory.GetFiles(_racine, "desired-state.manifest").Should().ContainSingle();
    }

    // ── la cible doit exister encore ────────────────────────────────

    [Fact]
    public async Task Une_cible_disparue_n_est_pas_reecrite_et_le_rapport_le_nomme()
    {
        // La cle de registre a ete supprimee depuis. Y reecrire la valeur d'origine poserait un
        // reglage ORPHELIN : une valeur que DrDXT n'a jamais mise la, laissee par la desinstallation.
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = new LectureFactice { [Cle(Reflex(2))] = "2" };
        lecture.Disparues.Add(Cle(Reflex(1)));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty("on n'ecrit jamais dans une cible qui n'existe plus");
        rapport.Should().Contain("[CibleDisparue] latency.reflex").And.Contain("valeur orpheline");
    }

    [Fact]
    public async Task Une_existence_de_cible_inconnue_ne_fait_rien_ecrire_non_plus()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.CibleExiste(default!).ReturnsForAnyArgs(
            Result<bool>.Fail(ErrorCodes.IoFailure, "ruche non ouverte"));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("[Illisible] latency.reflex").And.Contain("existence de la cible inconnue");
    }

    // Une mutation qui ecrivait quand meme sur une valeur courante illisible restait verte.
    // Ne pas savoir ce qu'il y a, c'est ne pas savoir si quelqu'un l'a change depuis : on n'ecrit pas.
    [Fact]
    public async Task Une_valeur_courante_illisible_ne_fait_rien_ecrire()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.CibleExiste(default!).ReturnsForAnyArgs(Result<bool>.Ok(true));
        lecture.Lire(default!).ReturnsForAnyArgs(Result<string?>.Fail(ErrorCodes.IoFailure, "acces refuse"));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().BeEmpty("une valeur qu'on ne sait pas lire n'est pas « encore celle posee »");
        rapport.Should().Contain("[Illisible] latency.reflex").And.Contain("valeur courante illisible");
    }

    // ── jamais bloquant ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_manifeste_illisible_ne_rend_rien_le_rapport_le_dit_et_s_ecrit()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var manifeste = Path.Combine(_racine, "desired-state.manifest");
        await File.WriteAllTextAsync(manifeste, (await File.ReadAllTextAsync(manifeste)).Replace("latency.reflex", "latency.reflez"));
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice { [Cle(Reflex(2))] = "2" }, ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("NON LU").And.Contain("la désinstallation continue");
        ModeDesinstallation.EcrireJournal(_racine, Quand, rapport, appliquerAcl: false).IsOk.Should().BeTrue();
        File.ReadAllText(Path.Combine(_racine, "desinstallation-20260911T230000Z.txt")).Should().Contain("NON LU");
    }

    [Fact]
    public async Task Sans_manifeste_ni_cle_rien_n_est_rendu_et_rien_ne_jette()
    {
        var ecriture = new EcritureFactice();

        var rapport = await Rendre(new LectureFactice(), ecriture);

        ecriture.Recues.Should().BeEmpty();
        rapport.Should().Contain("NON LU").And.Contain("nothing was ever signed");
    }

    // Une seconde désinstallation après une réinstallation : la clé est restée, le manifeste a été mis de côté la première
    // fois. Le rapport disait « lu et vérifié » puis « mis de côté » pour un fichier qui n'existait pas.
    [Fact]
    public async Task Sans_manifeste_avec_la_cle_le_rapport_ne_dit_ni_lu_ni_mis_de_cote()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        File.Delete(Path.Combine(_racine, "desired-state.manifest"));

        var rapport = await Rendre(new LectureFactice(), new EcritureFactice());

        rapport.Should().Contain("aucun manifeste").And.Contain("aucune origine à rendre.").And.NotContain("NON LU")
            .And.NotContain("lu et vérifié").And.NotContain("mis de côté (renommé)");
    }

    [Fact]
    public void Le_rapport_du_journal_ne_se_dit_pas_manifeste_signe()
    {
        var r = new RapportDuRendu(null, [new LigneDuRendu("journal", "cible", IssueDuRendu.Rendu, "")]);

        var texte = r.EnTexte(Quand, "journal d'écriture anticipée : lu", "journal : NON LU ({0})");
        texte.Should().Contain("journal d'écriture anticipée : lu").And.NotContain("lu et vérifié");
        new RapportDuRendu("illisible", []).EnTexte(Quand, "lu", "journal : NON LU ({0})")
            .Should().Contain("journal : NON LU (illisible)");
    }

    [Fact]
    public async Task Un_echec_d_ecriture_n_arrete_pas_les_entrees_suivantes()
    {
        await Signer(Gardes(
            new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand),
            new AppliedTweak("fixit.power_plan", Op(new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance)),
                Op(new SetActivePowerPlanOp(Equilibre)), Quand.AddMinutes(1))));
        var lecture = new LectureFactice
        {
            [Cle(Reflex(2))] = "2",
            ["plan"] = KnownPowerPlans.HighPerformance.ToString("D"),
        };
        var ecriture = new EcritureFactice { Echoue = op => op is SetRegistryValueOp };

        var rapport = await Rendre(lecture, ecriture);

        ecriture.Recues.Should().Contain(new SetActivePowerPlanOp(Equilibre));
        rapport.Should().Contain("[EchecDEcriture] latency.reflex").And.Contain("[Rendu] fixit.power_plan");
    }

    [Fact]
    public async Task Une_lecture_qui_jette_est_ecrite_au_rapport_et_ne_s_echappe_pas()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.CibleExiste(default!).ReturnsForAnyArgs(Result<bool>.Ok(true));
        lecture.Lire(default!).ReturnsForAnyArgs(_ => throw new InvalidOperationException("pilote parti"));

        var rapport = await Rendre(lecture, new EcritureFactice());

        rapport.Should().Contain("[EchecDEcriture] latency.reflex").And.Contain("pilote parti");
    }

    // ── le plafond d'envoi : hors manifeste, retire quand meme ──────────────────────────────────

    [Fact]
    public async Task La_desinstallation_retire_le_plafond_d_envoi_meme_absent_du_manifeste()
    {
        var ecriture = new EcritureFactice();

        var ligne = await ModeDesinstallation.RetirerPlafondEnvoiAsync(ecriture, CancellationToken.None);

        ecriture.Recues.Should().ContainSingle().Which.Should().BeOfType<RemoveUploadCapOp>();
        ligne.Should().Contain("aucun ne reste actif");
    }

    [Fact]
    public async Task Un_retrait_refuse_du_plafond_est_dit_dans_le_rapport_sans_arreter_la_desinstallation()
    {
        var refus = new EcritureFactice { Echoue = op => op is RemoveUploadCapOp };
        (await ModeDesinstallation.RetirerPlafondEnvoiAsync(refus, CancellationToken.None))
            .Should().Contain("pas retiré").And.Contain("redémarrage");

        var panne = Substitute.For<IElevatedClient>();
        panne.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new IOException("tube ferme"));
        (await ModeDesinstallation.RetirerPlafondEnvoiAsync(panne, CancellationToken.None))
            .Should().Contain("IOException");
    }

    // ── les programmes coupés au démarrage : hors manifeste, leurs traces les portent ──────────────────

    private static readonly byte[] PoseeParDrDxt = SetStartupApprovalOp.Octets(false, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    private static CoupureDuDemarrage Coupure(string nom, RegistryHiveKind ruche = RegistryHiveKind.CurrentUser)
        => new(ruche, SetStartupApprovalOp.Run, nom, Convert.ToHexString(PoseeParDrDxt));

    [Fact]
    public async Task Un_programme_encore_coupe_par_DrDXT_est_reactive_a_la_desinstallation()
    {
        var ecriture = new EcritureFactice();

        var texte = await ModeDesinstallation.ReactiverLesCoupuresAsync(
            [Coupure("Discord"), Coupure("Steam", RegistryHiveKind.LocalMachine)], _ => PoseeParDrDxt, ecriture, CancellationToken.None);

        ecriture.Recues.Should().Equal(
            new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "Discord", true),
            new SetStartupApprovalOp(RegistryHiveKind.LocalMachine, SetStartupApprovalOp.Run, "Steam", true));
        texte.Should().Contain("Discord : réactivé").And.Contain("Steam : réactivé");
    }

    // Recoupé ou réactivé à la main depuis (les octets portent l'heure de la coupure), ou valeur disparue : le choix du joueur
    // tient, rien n'est écrit.
    [Fact]
    public async Task Un_programme_change_depuis_la_coupure_est_laisse_tel_quel()
    {
        var ecriture = new EcritureFactice();
        var recoupe = SetStartupApprovalOp.Octets(false, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));

        var texte = await ModeDesinstallation.ReactiverLesCoupuresAsync([Coupure("Discord"), Coupure("Steam")],
            c => c.Nom == "Discord" ? recoupe : null, ecriture, CancellationToken.None);

        ecriture.Recues.Should().BeEmpty();
        texte.Should().Contain("Discord : changé depuis").And.Contain("Steam : changé depuis");
    }

    [Fact]
    public async Task Une_reactivation_refusee_est_dite_et_les_autres_sont_tentees()
    {
        var ecriture = new EcritureFactice { Echoue = op => op is SetStartupApprovalOp { Nom: "Discord" } };

        var texte = await ModeDesinstallation.ReactiverLesCoupuresAsync([Coupure("Discord"), Coupure("Steam")],
            _ => PoseeParDrDxt, ecriture, CancellationToken.None);

        ecriture.Recues.Should().HaveCount(2);
        texte.Should().Contain("Discord : toujours coupé").And.Contain("refus (test)").And.Contain("Steam : réactivé");
    }

    // Élevée, la désinstallation n'écrit jamais dans le dossier du compte : il peut être une jonction vers la racine de
    // confiance. Seule la trace de la machine se met à jour.
    [Fact]
    public void La_desinstallation_ne_reecrit_que_la_trace_de_la_machine()
    {
        ModeDesinstallation.TraceDuRendu(RegistryHiveKind.CurrentUser).Should().BeNull();
        ModeDesinstallation.TraceDuRendu(RegistryHiveKind.LocalMachine).Should().NotBeNull();

        // Le montage passe bien par elle, et la trace du compte n'y est que lue.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ElevatedHelper", "ModeDesinstallation.cs"));
        source.Should().Contain("new Win32RegistryActions(TraceDuRendu)");
        System.Text.RegularExpressions.Regex.Matches(source, @"TraceDesCoupures\.DeLaSession\(\)(?<suite>\.Lire\(\))?")
            .Should().ContainSingle("témoin : la trace du compte est lue pour réactiver ses coupures")
            .Which.Groups["suite"].Success.Should().BeTrue("jamais passée à un écrivain");
    }

    [Fact]
    public async Task Elevee_avec_un_autre_compte_la_desinstallation_ne_reactive_rien_dans_sa_ruche()
    {
        var ecriture = new EcritureFactice();

        await ModeDesinstallation.ReactiverLesCoupuresAsync([Coupure("Discord")], _ => PoseeParDrDxt,
            ModeDesinstallation.GarderLaRucheDuJoueur(ecriture, memeCompte: false), CancellationToken.None);

        ecriture.Recues.Should().BeEmpty();
    }

    // ── la carte NVIDIA : les baux du helper tué ne se rendent plus, le désinstalleur la rend ──────────

    [Fact]
    public async Task La_desinstallation_rend_le_ventilateur_et_le_decalage_d_une_carte_nvidia()
    {
        var ecriture = new EcritureFactice();

        var ligne = await ModeDesinstallation.RendreLaCarteNvidiaAsync(ecriture, [new(-150, true)], CancellationToken.None);

        ecriture.Recues.Should().Equal(new RestoreGpuFanAutoOp(0), new ApplyNvApiClockOffsetOp(0, 0, 0));
        ligne.Should().Contain("automatique").And.Contain("0 MHz");
    }

    [Fact]
    public async Task Un_decalage_deja_a_zero_n_est_pas_reecrit_et_sans_carte_nvidia_rien_ne_part()
    {
        var ecriture = new EcritureFactice();
        await ModeDesinstallation.RendreLaCarteNvidiaAsync(ecriture, [new(0, true)], CancellationToken.None);
        ecriture.Recues.Should().Equal(new RestoreGpuFanAutoOp(0));

        var sansNvidia = new EcritureFactice();
        (await ModeDesinstallation.RendreLaCarteNvidiaAsync(sansNvidia, [], CancellationToken.None)).Should().BeEmpty();
        sansNvidia.Recues.Should().BeEmpty();
    }

    [Fact]
    public async Task Une_carte_pas_rendue_est_dite_et_le_reste_part_quand_meme()
    {
        var refus = new EcritureFactice { Echoue = op => op is RestoreGpuFanAutoOp };

        var ligne = await ModeDesinstallation.RendreLaCarteNvidiaAsync(refus, [new(-150, true)], CancellationToken.None);

        refus.Recues.Should().Contain(new ApplyNvApiClockOffsetOp(0, 0, 0), "un ventilateur refusé n'empêche pas de rendre le décalage");
        ligne.Should().Contain("pas entièrement rendue").And.Contain("refus (test)").And.Contain("redémarrage");
    }

    [Fact]
    public async Task Une_carte_au_decalage_illisible_rend_quand_meme_son_ventilateur()
    {
        var ecriture = new EcritureFactice();

        await ModeDesinstallation.RendreLaCarteNvidiaAsync(ecriture, [new(null, true)], CancellationToken.None);

        ecriture.Recues.Should().Equal(new RestoreGpuFanAutoOp(0));
    }

    // Un portable sans ventilateur lisible : DrDXT n'a pas pu en poser un qu'il ne lit pas, rien n'est tenté ni dit à son sujet.
    [Fact]
    public async Task Sans_ventilateur_lisible_seul_le_decalage_est_rendu_sans_rien_dire_du_ventilateur()
    {
        var ecriture = new EcritureFactice();

        var ligne = await ModeDesinstallation.RendreLaCarteNvidiaAsync(ecriture, [new(-150, false)], CancellationToken.None);

        ecriture.Recues.Should().Equal(new ApplyNvApiClockOffsetOp(0, 0, 0));
        ligne.Should().NotContain("pas entièrement rendue");
    }

    [Fact]
    public void Chaque_carte_se_lit_seule_jusqu_a_la_premiere_qui_ne_repond_a_rien()
    {
        // Carte 0 : décalage illisible, ventilateurs lus ; carte 1 : tout lu ; carte 2 : rien (il n'y en a pas).
        int?[] decalages = [null, -100, null];
        bool[] ventilateurs = [true, true, false];

        var cartes = ModeDesinstallation.LireLesCartes(i => i < 3 ? decalages[i] : null, i => i < 3 && ventilateurs[i]);

        cartes.Should().Equal(new ModeDesinstallation.CarteNvidia(null, true), new ModeDesinstallation.CarteNvidia(-100, true));
        ModeDesinstallation.LireLesCartes(_ => 0, _ => true).Should().HaveCount(8, "borné : NVAPI n'énumère jamais plus");
    }

    // ── ce qui n'est pas au manifeste : la marque que l'installeur lit ──────────────────────────────────

    // Le manifeste resté en place ne couvre que ses propres lignes. Une carte, le plafond d'envoi ou un programme au démarrage
    // non rendus le laissaient partir, et l'installeur affichait sa réussite : chaque étape note ce qui reste.
    [Fact]
    public async Task Chaque_etape_hors_manifeste_qui_ne_rend_pas_note_qu_il_reste_quelque_chose()
    {
        var refus = new EcritureFactice { Echoue = _ => true };

        var plafond = new ModeDesinstallation.ResteARendre();
        await ModeDesinstallation.RetirerPlafondEnvoiAsync(refus, CancellationToken.None, plafond);
        plafond.Quelquechose.Should().BeTrue("le plafond d'envoi est resté");

        var carte = new ModeDesinstallation.ResteARendre();
        await ModeDesinstallation.RendreLaCarteNvidiaAsync(refus, [new(-150, true)], CancellationToken.None, carte);
        carte.Quelquechose.Should().BeTrue("la carte n'est pas rendue");

        var demarrage = new ModeDesinstallation.ResteARendre();
        await ModeDesinstallation.ReactiverLesCoupuresAsync([Coupure("Discord")], _ => PoseeParDrDxt, refus, CancellationToken.None, demarrage);
        demarrage.Quelquechose.Should().BeTrue("le programme reste coupé");
    }

    [Fact]
    public async Task Tout_rendu_ne_note_rien_et_un_programme_change_depuis_n_est_pas_un_reste()
    {
        var reste = new ModeDesinstallation.ResteARendre();
        var ecriture = new EcritureFactice();

        await ModeDesinstallation.RetirerPlafondEnvoiAsync(ecriture, CancellationToken.None, reste);
        await ModeDesinstallation.RendreLaCarteNvidiaAsync(ecriture, [new(-150, true)], CancellationToken.None, reste);
        await ModeDesinstallation.ReactiverLesCoupuresAsync([Coupure("Discord"), Coupure("Steam")],
            c => c.Nom == "Discord" ? PoseeParDrDxt : null, ecriture, CancellationToken.None, reste);

        reste.Quelquechose.Should().BeFalse("tout est rendu, ou laissé au choix du joueur");
    }

    [Fact]
    public async Task Une_origine_du_manifeste_non_rendue_note_aussi_qu_il_reste_quelque_chose()
    {
        await Signer(Gardes(new AppliedTweak("latency.reflex", Op(Reflex(2)), Op(Reflex(1)), Quand)));
        var reste = new ModeDesinstallation.ResteARendre();

        await ModeDesinstallation.ExecuterAsync(_racine, appliquerAcl: false, new LectureFactice { [Cle(Reflex(2))] = "2" },
            new EcritureFactice { Echoue = op => op is SetRegistryValueOp }, Quand, CancellationToken.None, reste);

        reste.Quelquechose.Should().BeTrue();
    }

    [Fact]
    public void La_marque_nomme_le_rapport_quand_il_reste_quelque_chose_et_part_quand_tout_est_rendu()
    {
        var marque = Path.Combine(_racine, ModeDesinstallation.MarqueRenduIncomplet);

        ModeDesinstallation.NoterLIssue(_racine, Quand, incomplet: true, appliquerAcl: false).IsOk.Should().BeTrue();
        File.ReadAllText(marque).Should().Contain("desinstallation-20260911T230000Z.txt");

        // Réinstallé puis désinstallé, tout rendu cette fois : la marque d'avant ne fait plus dire « incomplet ».
        ModeDesinstallation.NoterLIssue(_racine, Quand.AddDays(1), incomplet: false, appliquerAcl: false).IsOk.Should().BeTrue();
        File.Exists(marque).Should().BeFalse();
    }

    // ── doublures ─────────────────────────────────────────────────────────────────────────────

    private sealed class LectureFactice : Dictionary<string, string?>, ILectureDesCibles
    {
        /// <summary>Les cibles DISPARUES : la cle supprimee, le service
        /// desinstalle, le plan efface. Par defaut une cible existe — c'est le cas courant.</summary>
        public HashSet<string> Disparues { get; } = new();

        public Result<string?> Lire(PrivilegedOp ecriture)
            => TryGetValue(Cle(ecriture), out var v)
                ? Result<string?>.Ok(v)
                : Result<string?>.Fail(ErrorCodes.IoFailure, "non lu (test)");

        public Result<bool> CibleExiste(PrivilegedOp ecriture)
            => Result<bool>.Ok(!Disparues.Contains(Cle(ecriture)));
    }

    private sealed class EcritureFactice : IElevatedClient
    {
        public List<PrivilegedOp> Recues { get; } = new();
        public Func<PrivilegedOp, bool> Echoue { get; init; } = _ => false;
        /// <summary>Le pilote accepte l'écriture sans rien changer.</summary>
        public bool SansEffet { get; init; }
        public LectureFactice? Machine { get; set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recues.Add(op);
            if (Echoue(op)) return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refus (test)"));
            if (Machine is not null)
            {
                if (op is DeleteRegistryValueOp or DeleteNvApiDrsSettingOp) Machine[Cle(op)] = null; // relue « absente »
                else if (Valeur(op) is { } v) Machine[Cle(op)] = v;
            }
            return Task.FromResult(Result.Ok());
        }

        private static string? Valeur(PrivilegedOp op) => op switch
        {
            SetRegistryValueOp s => s.Value,
            ApplyNvApiDrsSettingOp a => Dec(a.Value),
            ApplyAdlxTuningOp a => a.IntValue.ToString(CultureInfo.InvariantCulture),
            ApplyNvApiPowerLimitOp n => n.PowerLimitPercent.ToString(CultureInfo.InvariantCulture),
            SetActivePowerPlanOp p => p.PlanGuid.ToString("D", CultureInfo.InvariantCulture),
            SetProcessorBoostModeOp b => b.AcValue.ToString(CultureInfo.InvariantCulture),
            RunServiceControlOp { Action: ServiceAction.Start } => "running",
            RunServiceControlOp { Action: ServiceAction.Stop } => "stopped",
            _ => null,
        };
    }
}
