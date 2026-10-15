using System.Globalization;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
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
        => ModeDesinstallation.ExecuterAsync(_racine, appliquerAcl: false, lecture, ecriture, Quand, CancellationToken.None);

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

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recues.Add(op);
            return Task.FromResult(Echoue(op) ? Result.Fail(ErrorCodes.HelperOpFailed, "refus (test)") : Result.Ok());
        }
    }
}
