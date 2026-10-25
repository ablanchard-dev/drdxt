using System.Globalization;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.Reconciler;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// CommitManifestOp, la nouvelle op privilegiee : l'App envoie les entrees, le helper eleve
// valide, fusionne, signe avec la cle de la machine et ecrit.
//
// Trois questions, dans l'ordre ou un attaquant les poserait :
//   1. l'allowlist refuse-t-elle ce qui serait EXECUTE plus tard (au demarrage par le service SYSTEM, a
//      la desinstallation elevee) et n'est pas permis ? — avec un TEMOIN qui passe, sinon une allowlist
//      qui refuse tout serait verte ;
//   2. l'op traverse-t-elle le tube authentifie dans les deux sens (l'appelant par le serveur, le serveur
//      par le client) et un appelant non authentifie est-il refuse avant l'ecrivain ?
//   3. le code d'un refus (MANIFEST_UNREADABLE) revient-il jusqu'a l'App, qui en tire la reprise ?
//
// Tout tourne en processus, sur un tube de test, sans elevation ni ecriture hors d'un dossier temporaire.
// -----------------------------------------------------------------------
public sealed class CommitManifestOpTests
{
    private static readonly OpAllowlist Allowlist = new();
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;
    private const string Nvtweak = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";

    private static string OpJson(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    // Une origine reelle : l'inverse que capture NvidiaUltraLowLatencyTweak.
    private static string OrigineReflex(uint v) => OpJson(new SetRegistryValueOp(
        RegistryHiveKind.LocalMachine, Nvtweak, "LowLatencyMode", RegistryValueType.DWord,
        v.ToString(CultureInfo.InvariantCulture)));

    private static AppliedTweak Correction(string id = "latency.reflex.lowLatencyMode", string voulu = "2", string? origine = null)
        => new(id, voulu, origine ?? OrigineReflex(1), T);

    private static AppliedTweak Garde(string id = "latency.reflex", string? origine = null)
        => new(id, OrigineReflex(2), origine ?? OrigineReflex(1), T);

    private static CommitManifestOp Op(AppliedTweak[]? tweaks = null, AppliedTweak[]? revertOnly = null, bool repartir = false)
        => new(tweaks ?? Array.Empty<AppliedTweak>(), revertOnly ?? Array.Empty<AppliedTweak>(), repartir);

    // ── 1. l'allowlist ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Temoin_un_commit_legitime_passe()
    {
        Allowlist.Validate(Op(new[] { Correction() }, new[] { Garde() })).IsOk.Should().BeTrue(
            "sans ce temoin, chaque refus ci-dessous pourrait venir d'une allowlist qui refuse tout");
    }

    [Fact]
    public void Temoin_les_quatre_jetons_que_l_App_commite_reellement_passent()
    {
        // Ceux d'AppliedTweakManifestMapper, par le meme canoniseur. Une allowlist plus stricte que le
        // produit casserait chaque commit en silence (l'ecran dirait « non arme », rien de plus).
        var tweaks = new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", ReconcilableValueCanonicalizer.PowerPlan(KnownPowerPlans.HighPerformance), "{}", T),
            new AppliedTweak("latency.reflex.lowLatencyMode", "2", "{}", T),
            new AppliedTweak("fixit.x3dVCacheService", ReconcilableValueCanonicalizer.Service(ServiceRunState.Running), "{}", T),
            new AppliedTweak("os.hags", ReconcilableValueCanonicalizer.Dword(2), "{}", T),
        };
        Allowlist.Validate(Op(tweaks)).IsOk.Should().BeTrue();
    }

    // Le repos de la carte prouvé et un geste DLSS ne sont pas des candidats de l'autopilote, mais leur origine doit entrer au
    // manifeste pour que la désinstallation la rende. Un paquet de pilote retiré, lui, reste dehors.
    [Fact]
    public void Le_repos_de_la_carte_et_un_geste_DLSS_entrent_au_manifeste_un_paquet_de_pilote_non()
    {
        var repos = new AppliedTweak("gpu.repos_global_normal", OpJson(new ApplyNvApiDrsSettingOp(0x1057EB71, 0, "Base Profile")),
            OpJson(new ApplyNvApiDrsSettingOp(0x1057EB71, 1, "Base Profile")), T);
        var dlss = new AppliedTweak(@"geste.dlss|C:\Jeux\cod.exe", "{\"composite\":[1]}", "{\"composite\":[2]}", T);
        Allowlist.Validate(Op(revertOnly: new[] { repos, dlss })).IsOk.Should().BeTrue();

        var pilote = new AppliedTweak("geste.pilote|oem12.inf", "x", OpJson(new RestoreDriverPackageOp("oem12.inf")), T);
        Allowlist.Validate(Op(revertOnly: new[] { pilote })).IsOk.Should().BeFalse("la remise d'un pilote se fait depuis l'App");
        Allowlist.Validate(Op(revertOnly: new[] { new AppliedTweak("geste.autre|x", "a", "b", T) })).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Une_origine_composite_indecodable_passe_comme_donnee_bornee()
    {
        // La desinstallation la refusera en le disant (SAFETY.SNAPSHOT_INVALID) : elle n'est jamais executee.
        Allowlist.Validate(Op(revertOnly: new[] { Garde(origine: "{\"composite\":[1,2]}") })).IsOk.Should().BeTrue();
    }

    public static TheoryData<string, CommitManifestOp> Refus => new()
    {
        { "correction hors de la table du reconciliateur", Op(new[] { Correction(id: "inconnu.levier") }) },
        { "correction sur une surface protegee", Op(new[] { Correction(id: "service.WinDefend") }) },
        { "valeur voulue qui ne se traduit pas en op", Op(new[] { Correction(voulu: "pas-un-dword") }) },
        { "levier garde inconnu du catalogue", Op(revertOnly: new[] { Garde(id: "inconnu.levier") }) },
        { "origine = redemarrage dans le firmware", Op(revertOnly: new[] { Garde(origine: OpJson(new RebootToFirmwareSetupOp(0))) }) },
        { "origine = un autre commit du manifeste", Op(revertOnly: new[] { Garde(origine: OpJson(Op(new[] { Correction() }))) }) },
        { "origine = bail du chien de garde", Op(revertOnly: new[] { Garde(origine: OpJson(new EstablishLeaseOp("l", OrigineReflex(1), 30))) }) },
        { "origine = Defender coupe", Op(revertOnly: new[] { Garde(origine: OpJson(new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start", RegistryValueType.DWord, "4"))) }) },
        { "origine = feuille de registre non permise", Op(new[] { Correction(origine: OpJson(new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, Nvtweak, "AutreValeur", RegistryValueType.DWord, "1"))) }) },
        { "valeur trop longue", Op(revertOnly: new[] { Garde(origine: new string('x', 5000)) }) },
        { "origine vide", Op(revertOnly: new[] { Garde(origine: " ") }) },
        // La carte se rend à la désinstallation, mais jamais sa tension ni sa fréquence (règle de la carte).
        { "origine = sous-tension ADLX", Op(revertOnly: new[] { Garde(id: "gpu.power_limit", origine: OpJson(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2500))) }) },
        { "origine = décalage d'horloge NVIDIA", Op(revertOnly: new[] { Garde(id: "gpu.power_limit", origine: OpJson(new ApplyNvApiClockOffsetOp(0, 100, 0))) }) },
        { "trop d'entrees", Op(revertOnly: Enumerable.Repeat(Garde(), 33).ToArray()) },
        { "doublon dans une liste", Op(revertOnly: new[] { Garde(), Garde() }) },
        { "commit vide sans reprise", Op() },
    };

    // Les réglages de la carte gardés par le joueur entrent au manifeste, pour être rendus à la désinstallation.
    [Fact]
    public void Les_reglages_de_la_carte_qui_ne_sont_ni_tension_ni_frequence_entrent_au_manifeste()
    {
        var gardes = new[]
        {
            Garde(id: "gpu.power_limit", origine: OpJson(new ApplyNvApiPowerLimitOp(0, 100))),
            Garde(id: "latency.render_queue_cap", origine: OpJson(new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 0))),
        };
        Allowlist.Validate(Op(revertOnly: gardes)).IsOk.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Refus))]
    public void Ce_qui_serait_execute_et_n_est_pas_permis_est_refuse(string pourquoi, CommitManifestOp op)
    {
        var r = Allowlist.Validate(op);
        r.IsOk.Should().BeFalse(pourquoi);
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted, pourquoi);
    }

    // Les origines que « Optimiser » inscrit désormais pour les leviers de la session
    // Windows, sous la forme exacte que leurs leviers capturent. Refusées ici, elles n'atteindraient jamais le
    // manifeste, et la désinstallation ne les rendrait pas.
    public static TheoryData<string, string, string> OriginesDeSession()
    {
        const string prefs = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string barre = @"Software\Microsoft\GameBar";
        var vrr = OpJson(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, prefs, "DirectXUserGlobalSettings",
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;"));
        var vrrPose = OpJson(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, prefs, "DirectXUserGlobalSettings",
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1"));
        var dvr = OpJson(new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"));
        var dvrPose = OpJson(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueType.DWord, "0"));
        var jeu = CompositeSnapshotCodec.Encode(new PrivilegedOp[]
        {
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, barre, "AllowAutoGameMode"),
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, barre, "AutoGameModeEnabled"),
        }).Value!;
        var jeuPose = CompositeSnapshotCodec.Encode(new PrivilegedOp[]
        {
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, barre, "AllowAutoGameMode", RegistryValueType.DWord, "1"),
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, barre, "AutoGameModeEnabled", RegistryValueType.DWord, "1"),
        }).Value!;
        return new()
        {
            { "latency.vrrFlipModelOn", vrrPose, vrr },
            { "latency.gameDvrBackgroundOff", dvrPose, dvr },
            { "latency.gameModeOn", jeuPose, jeu },
        };
    }

    [Theory]
    [MemberData(nameof(OriginesDeSession))]
    public void Temoin_l_origine_d_un_levier_de_session_garde_passe(string id, string pose, string origine)
        => Allowlist.Validate(Op(revertOnly: new[] { new AppliedTweak(id, pose, origine, T) })).IsOk.Should().BeTrue();

    // Retirer un levier annulé. Seuls des identifiants courts et imprimables, bornés.
    [Fact]
    public void Temoin_retirer_un_levier_annule_passe()
        => Allowlist.Validate(new CommitManifestOp(Array.Empty<AppliedTweak>(), Array.Empty<AppliedTweak>(),
            Retirer: new[] { "latency.vrrFlipModelOn", "os.hags" })).IsOk.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("a\u0001b")]
    public void Un_retrait_mal_forme_est_refuse(string id)
        => Allowlist.Validate(new CommitManifestOp(Array.Empty<AppliedTweak>(), Array.Empty<AppliedTweak>(),
            Retirer: new[] { id })).IsOk.Should().BeFalse();

    [Fact]
    public void Un_retrait_trop_long_est_refuse()
        => Allowlist.Validate(new CommitManifestOp(Array.Empty<AppliedTweak>(), Array.Empty<AppliedTweak>(),
            Retirer: Enumerable.Repeat("os.hags", 33).ToArray())).IsOk.Should().BeFalse();

    // Le helper retire les anciens points d'après leur nom ; il n'accepte donc de créer que
    // sous le nom fixe de DrDXT, sinon un appelant ferait retirer les points d'un autre logiciel.
    [Fact]
    public void Un_point_de_restauration_ne_se_cree_que_sous_le_nom_de_DrDXT()
    {
        Allowlist.Validate(new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt)).IsOk.Should().BeTrue();
        Allowlist.Validate(new CreateRestorePointOp("Windows Update")).IsOk.Should().BeFalse();
    }

    // Le manifeste est écrit sans droits : une tension comme « origine » ferait poser une sous-tension par le helper
    // élevé à la désinstallation. Aucun levier ne garde la tension dans le manifeste.
    [Fact]
    public void Une_tension_processeur_n_est_jamais_une_origine()
        => Allowlist.Validate(Op(revertOnly: new[] { Garde(origine: OpJson(new SetCpuVoltageOffsetOp(-125))) }))
            .IsOk.Should().BeFalse();

    [Fact]
    public void La_reprise_peut_commencer_un_manifeste_vide()
        => Allowlist.Validate(Op(repartir: true)).IsOk.Should().BeTrue();

    [Fact]
    public void Des_listes_absentes_sont_refusees()
        => Allowlist.Validate(new CommitManifestOp(null!, null!)).IsOk.Should().BeFalse();

    [Fact]
    public void L_op_traverse_le_codec_borne_intacte()
    {
        var op = Op(new[] { Correction() }, new[] { Garde() }, repartir: true) with { Retirer = new[] { "os.hags" } };
        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();
        json.Value.Should().Contain($"\"op\":{(int)PrivilegedOpType.CommitManifest}");

        var relu = PrivilegedOpCodec.Deserialize(json.Value).Value.Should().BeOfType<CommitManifestOp>().Subject;
        relu.RepartirDUnManifesteVide.Should().BeTrue();
        relu.Tweaks.Should().ContainSingle().Which.Should().Be(op.Tweaks[0]);
        relu.RevertOnly.Should().ContainSingle().Which.Should().Be(op.RevertOnly[0]);
        relu.Retirer.Should().Equal("os.hags");
    }

    // ── l'executeur ──────────────────────────────────────────────────────────────────────────

    private static PrivilegedOpExecutor Executeur(IManifestCommitActions? manifeste) => new(
        Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
        Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
        Substitute.For<IADLXTuningActions>(), firmwareBoot: null, manifeste: manifeste);

    [Fact]
    public async Task Un_hote_sans_ecrivain_de_manifeste_echoue_fort()
    {
        // C'est le cas du service SYSTEM : il verifie, il ne signe jamais.
        var r = await Executeur(null).ExecuteAsync(Op(new[] { Correction() }), default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("not wired");
    }

    // ── 2 et 3. le tube authentifie ──────────────────────────────────────────────────────────

    private sealed class Autoriser : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class Refuser : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Fail(ErrorCodes.ElevationDenied, "denied");
    }

    private static string Tube() => "DrDXT.CommitTest." + Guid.NewGuid().ToString("N");

    // Le serveur tourne dans ce processus : son image attendue par le client est celle du processus de test.
    private static string CeProcessus => Environment.ProcessPath!;

    private static async Task<Result> Envoyer(ICallerAuthenticator appelant, IManifestCommitActions ecrivain,
        CommitManifestOp op, string? serveurAttendu = null)
    {
        var tube = Tube();
        var serveur = new HandshakeAcceptingServer(appelant, new CryptoSessionKeyGenerator(), new OpAllowlist(),
            Executeur(ecrivain), tube);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepte = serveur.AcceptOneAsync(cts.Token);

        var r = await new HandshakingElevatedClient(tube, serveurAttendu ?? CeProcessus).ExecuteAsync(op, cts.Token);
        // Un client qui refuse avant le handshake ferme le tube : le serveur finit sa connexion seul.
        await accepte;
        return r;
    }

    [Fact]
    public async Task Temoin_un_appelant_authentifie_atteint_l_ecrivain_avec_ses_entrees()
    {
        var ecrivain = Substitute.For<IManifestCommitActions>();
        ecrivain.Commit(Arg.Any<CommitManifestOp>()).Returns(Result.Ok());

        var r = await Envoyer(new Autoriser(), ecrivain, Op(new[] { Correction() }, new[] { Garde() }));

        r.IsOk.Should().BeTrue();
        ecrivain.Received(1).Commit(Arg.Is<CommitManifestOp>(c =>
            c.Tweaks.Single().TweakId == "latency.reflex.lowLatencyMode" && c.RevertOnly.Single().TweakId == "latency.reflex"));
    }

    [Fact]
    public async Task Un_appelant_non_authentifie_est_refuse_avant_l_ecrivain()
    {
        var ecrivain = Substitute.For<IManifestCommitActions>();

        var r = await Envoyer(new Refuser(), ecrivain, Op(new[] { Correction() }));

        r.IsOk.Should().BeFalse();
        ecrivain.DidNotReceiveWithAnyArgs().Commit(default!);
    }

    [Fact]
    public async Task Un_serveur_qui_n_est_pas_le_helper_attendu_n_apprend_meme_pas_les_entrees()
    {
        var ecrivain = Substitute.For<IManifestCommitActions>();

        var r = await Envoyer(new Autoriser(), ecrivain, Op(new[] { Correction() }),
            serveurAttendu: @"C:\Program Files\DrDXT\DrDxt.ElevatedHelper.exe");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        r.Error.Value.Message.Should().Contain("pipe server not authenticated");
        ecrivain.DidNotReceiveWithAnyArgs().Commit(default!);
    }

    [Fact]
    public async Task Un_commit_hostile_d_un_appelant_authentifie_n_atteint_pas_l_ecrivain()
    {
        var ecrivain = Substitute.For<IManifestCommitActions>();

        var r = await Envoyer(new Autoriser(), ecrivain,
            Op(revertOnly: new[] { Garde(origine: OpJson(new RebootToFirmwareSetupOp(0))) }));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        ecrivain.DidNotReceiveWithAnyArgs().Commit(default!);
    }

    [Fact]
    public async Task Le_code_d_un_manifeste_illisible_revient_jusqu_au_client()
    {
        var ecrivain = Substitute.For<IManifestCommitActions>();
        ecrivain.Commit(Arg.Any<CommitManifestOp>())
            .Returns(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));

        var r = await Envoyer(new Autoriser(), ecrivain, Op(new[] { Correction() }));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestUnreadable,
            "l'App ne propose la reprise que sur ce code : perdu en route, le refus deviendrait muet");
    }
}
