using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// 25/09/2026 (revue de sécurité) — pending-revert.json vit sous %LOCALAPPDATA%, écrivable par tout
/// processus du compte, et son contenu est rejoué par le helper élevé au démarrage. Un fichier piégé ne
/// doit jamais atteindre le levier.
/// </summary>
public sealed class RevertPiegeAuDemarrageTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-piege-{Guid.NewGuid():N}");
    private string Fichier => Path.Combine(_dossier, "pending-revert.json");

    public RevertPiegeAuDemarrageTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    private sealed class Levier : ITweak
    {
        public int Rendus;
        public string Id => "latency.hagsOn";
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak.latency.hags.enable";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => throw new NotSupportedException();
        public Task<Result> RevertAsync(TweakSnapshot snapshot) { Rendus++; return Task.FromResult(Result.Ok()); }
    }

    private sealed class Catalogue(ITweak levier) : ITweakCatalog
    {
        public Result<ITweak> Get(string tweakId) => tweakId == levier.Id
            ? Result<ITweak>.Ok(levier) : Result<ITweak>.Fail("x", "inconnu");
        public IReadOnlyList<ITweak> All() => [levier];
    }

    private (string? Phrase, Levier L, FichierRevertEnAttente J) Rejouer(PrivilegedOp op)
    {
        var journal = new FichierRevertEnAttente(Fichier);
        journal.Poser("latency.hagsOn", new TweakSnapshot("latency.hagsOn", PrivilegedOpCodec.Serialize(op).Value!));
        var levier = new Levier();
        return (RevertAuDemarrage.Executer(new FichierRevertEnAttente(Fichier), new Catalogue(levier)), levier, journal);
    }

    // 26/09/2026 : le demarrage ne doit jamais tomber sur un fichier abime, et un fichier abime ne doit
    // jamais atteindre le levier. Contenus bruts ecrits a la place du journal, exceptions comprises.
    public static TheoryData<string> Abimes() => new()
    {
        "",
        "null",
        "{",
        "[]",
        "\"texte\"",
        "{\"TweakId\":\"latency.hagsOn\"}",
        "{\"TweakId\":\"latency.hagsOn\",\"Snapshot\":null}",
        "{\"TweakId\":\"latency.hagsOn\",\"Snapshot\":{\"TweakId\":\"latency.hagsOn\",\"OriginalValueJson\":\"{\\\"op\\\":1}\"}}",
        "{\"TweakId\":123,\"Snapshot\":[]}",
        new string('[', 5000) + new string(']', 5000),
        "\u0000\u0001ÿ garbage",
    };

    [Theory]
    [MemberData(nameof(Abimes))]
    public void Un_journal_abime_ne_fait_ni_tomber_le_demarrage_ni_agir_le_levier(string contenu)
    {
        File.WriteAllText(Fichier, contenu);
        var levier = new Levier();

        var executer = () => RevertAuDemarrage.Executer(new FichierRevertEnAttente(Fichier), new Catalogue(levier));

        executer.Should().NotThrow();
        levier.Rendus.Should().Be(0);
    }

    // 26/09/2026 : deux cas que des mutations laissaient verts. Le plus grave : si la barrière acceptait un
    // composite dès qu'UN de ses ops est une remise en état, un fichier piégé cacherait un redémarrage dans le
    // BIOS derrière une écriture de registre légitime.
    private static readonly SetRegistryValueOp EcritureLegitime = new(
        RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord, "1");

    [Fact]
    public void Un_composite_qui_cache_un_redemarrage_BIOS_derriere_une_ecriture_legitime_est_refuse()
    {
        var piege = CompositeSnapshotCodec.Encode([EcritureLegitime, new RebootToFirmwareSetupOp(0)]).Value!;
        RevertAuDemarrage.InverseRejouable("latency.hagsOn", piege).Should().BeFalse("chaque op du composite doit être une remise en état");

        var journal = new FichierRevertEnAttente(Fichier);
        journal.Poser("latency.hagsOn", new TweakSnapshot("latency.hagsOn", piege));
        var levier = new Levier();
        RevertAuDemarrage.Executer(new FichierRevertEnAttente(Fichier), new Catalogue(levier));
        levier.Rendus.Should().Be(0, "le helper élevé ne doit jamais recevoir ce composite");
    }

    [Fact]
    public void Un_composite_vide_n_est_pas_une_remise_en_etat()
        => RevertAuDemarrage.InverseRejouable("latency.hagsOn", """{"ops":[]}""").Should().BeFalse("rien à rejouer n'est pas une remise en état");

    [Fact]
    public void Un_fichier_piege_qui_demande_un_redemarrage_dans_le_BIOS_n_atteint_jamais_le_levier()
    {
        var (phrase, levier, journal) = Rejouer(new RebootToFirmwareSetupOp(0));
        levier.Rendus.Should().Be(0, "le helper élevé ne doit jamais recevoir cet op");
        phrase.Should().Contain("n'est pas une remise en état");
        journal.Lire().Value.Should().NotBeNull("rien n'est rejoué, rien n'est effacé : le joueur est prévenu");
    }

    [Fact]
    public void Une_vraie_remise_en_etat_de_registre_passe_toujours()
    {
        var (phrase, levier, _) = Rejouer(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "1"));
        levier.Rendus.Should().Be(1);
        phrase.Should().Contain("remis comme avant");
    }

    [Fact]
    public void Un_contenu_illisible_est_refuse()
        => RevertAuDemarrage.InverseRejouable("latency.hagsOn", "{ pas un op").Should().BeFalse();

    // 26/09/2026 : la barrière ne regardait que le TYPE. Un fichier qui nomme le levier HAGS (registre) et
    // porte une limite de puissance GPU faisait écrire le GPU par le helper au lancement, sans clic.
    [Fact]
    public void Une_ecriture_GPU_rangee_sous_un_levier_de_registre_n_atteint_jamais_le_levier()
    {
        var (phrase, levier, journal) = Rejouer(new ApplyNvApiPowerLimitOp(0, 50));
        levier.Rendus.Should().Be(0, "HAGS se remet par le registre, jamais par le GPU");
        phrase.Should().Contain("n'est pas une remise en état");
        journal.Lire().Value.Should().NotBeNull();
    }

    [Theory]
    [InlineData("gpu.power_limit", true)]
    [InlineData("gpu.undervolt", false)]
    [InlineData("latency.gameModeOn", false)]
    [InlineData("levier.inconnu", false)]
    public void Chaque_levier_ne_rejoue_que_ce_qu_il_ecrit_lui_meme(string id, bool permis)
        => RevertAuDemarrage.InverseRejouable(id, PrivilegedOpCodec.Serialize(new ApplyNvApiPowerLimitOp(0, 80)).Value!)
            .Should().Be(permis);

    // 26/09/2026 : le type seul laissait un levier de registre rejouer n'importe quelle feuille permise. Un
    // fichier nommant HAGS et portant « PagingFiles vide » désactivait le fichier d'échange au lancement.
    private const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private static string Json(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    [Fact]
    public void Un_levier_de_registre_ne_rejoue_pas_la_feuille_d_un_autre()
    {
        var pagefile = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagement, "PagingFiles",
            RegistryValueType.MultiString, "");
        RevertAuDemarrage.InverseRejouable("latency.hagsOn", Json(pagefile)).Should().BeFalse();
        RevertAuDemarrage.InverseRejouable("fixit.sane_pagefile", Json(pagefile)).Should().BeTrue();

        var suppression = new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagement, "PagingFiles");
        RevertAuDemarrage.InverseRejouable("latency.gameDvrBackgroundOff", Json(suppression)).Should().BeFalse();
    }

    [Fact]
    public void Un_composite_qui_glisse_une_feuille_etrangere_est_refuse()
    {
        var gameBar = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar",
            "AllowAutoGameMode", RegistryValueType.DWord, "1");
        var dvr = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "HistoricalCaptureEnabled", RegistryValueType.DWord, "1");
        RevertAuDemarrage.InverseRejouable("fixit.x3d_ccd", CompositeSnapshotCodec.Encode([gameBar]).Value!).Should().BeTrue();
        RevertAuDemarrage.InverseRejouable("fixit.x3d_ccd", CompositeSnapshotCodec.Encode([gameBar, dvr]).Value!).Should().BeFalse();

        // Même clé, autre valeur : x3d_ccd n'écrit que AllowAutoGameMode sous GameBar.
        var voisine = gameBar with { Name = "AutoGameModeEnabled" };
        RevertAuDemarrage.InverseRejouable("fixit.x3d_ccd", Json(voisine)).Should().BeFalse();
        RevertAuDemarrage.InverseRejouable("latency.gameModeOn", Json(voisine)).Should().BeTrue();
    }

    // Même trou côté NVIDIA : le helper borne l'identifiant DRS, jamais la valeur. Un fichier nommant
    // « preferer les performances » et portant un limiteur d'images à 20 bridait toute la machine.
    [Fact]
    public void Un_levier_DRS_ne_rejoue_que_ses_propres_reglages()
    {
        const uint Frl = 0x10835002, Pstate = 0x1057EB71, PreRendu = 0x007ba09e;
        var limiteur = new ApplyNvApiDrsSettingOp(Frl, 20, "Base Profile");
        RevertAuDemarrage.InverseRejouable("latency.prefer_max_perf", Json(limiteur)).Should().BeFalse();
        RevertAuDemarrage.InverseRejouable("latency.render_queue_cap", Json(limiteur)).Should().BeFalse();
        RevertAuDemarrage.InverseRejouable("latency.vrr_vsync_cap", Json(limiteur)).Should().BeTrue();

        RevertAuDemarrage.InverseRejouable("latency.prefer_max_perf",
            Json(new ApplyNvApiDrsSettingOp(Pstate, 0, "Base Profile"))).Should().BeTrue();
        RevertAuDemarrage.InverseRejouable("latency.render_queue_cap",
            Json(new DeleteNvApiDrsSettingOp(PreRendu, @"C:\Jeux\jeu.exe", DrsValueOrigin.InheritedFromBaseProfile, 1))).Should().BeTrue();
        RevertAuDemarrage.InverseRejouable("latency.render_queue_cap",
            Json(new DeleteNvApiDrsSettingOp(Frl, @"C:\Jeux\jeu.exe", DrsValueOrigin.InheritedFromBaseProfile, 1))).Should().BeFalse();
    }

    // Même trou côté AMD : une op ADLX porte son genre (Anti-Lag, sous-tension, puissance). La file d'images
    // n'écrit que l'Anti-Lag, et rejouait pourtant une limite de puissance.
    [Theory]
    [InlineData("latency.render_queue_cap", AdlxTuningKind.AntiLag, true)]
    [InlineData("latency.render_queue_cap", AdlxTuningKind.PowerLimit, false)]
    [InlineData("latency.render_queue_cap", AdlxTuningKind.Undervolt, false)]
    [InlineData("gpu.undervolt", AdlxTuningKind.Undervolt, true)]
    [InlineData("gpu.undervolt", AdlxTuningKind.PowerLimit, false)]
    [InlineData("gpu.power_limit", AdlxTuningKind.PowerLimit, true)]
    [InlineData("gpu.power_limit", AdlxTuningKind.Undervolt, false)]
    [InlineData("latency.vrr_vsync_cap", AdlxTuningKind.FrameRateTarget, true)]
    [InlineData("latency.vrr_vsync_cap", AdlxTuningKind.VerticalRefresh, true)]
    [InlineData("latency.vrr_vsync_cap", AdlxTuningKind.PowerLimit, false)]
    [InlineData("latency.render_queue_cap", AdlxTuningKind.FrameRateTarget, false)]
    public void Un_levier_AMD_ne_rejoue_que_son_genre_de_reglage(string id, AdlxTuningKind genre, bool permis)
        => RevertAuDemarrage.InverseRejouable(id, Json(new ApplyAdlxTuningOp(0, genre, 60))).Should().Be(permis);

    [Fact]
    public void La_ruche_compte_aussi()
        => RevertAuDemarrage.InverseRejouable("latency.hagsOn", Json(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "1"))).Should().BeFalse();

    [Fact]
    public void Tout_essai_automatique_qui_pose_un_levier_sait_se_rendre_au_demarrage()
    {
        // Un candidat oublié dans la table resterait posé après une session interrompue : le démarrage
        // refuserait son inverse. Exceptions relevées dans le code le 26/09/2026 :
        //   gpu.core_offset : aucun levier ne porte cet identifiant ;
        //   (gpu.fan_curve en est sorti le 08/10/2026 : son inverse est RestoreGpuFanAutoOp, le mode Expert ;)
        //   fixit.ecran_frequence (07/10/2026) : jamais un essai automatique (AutoReglage) ; le mode n'est enregistré
        //   qu'après confirmation du joueur, et avant, un changement dynamique ne survit pas à la session.
        string[] sansRejeu = ["gpu.core_offset", "fixit.ecran_frequence"];
        CatalogueDeCandidats.Decrire("fixit.ecran_frequence")!.Kind.Should().Be(CandidateKind.AutoReglage);
        CatalogueDeCandidats.Ids.Except(sansRejeu)
            .Should().OnlyContain(id => RevertAuDemarrage.LeviersRejouables.Contains(id));
    }
}
