using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Detection.Settings;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Le mode de gestion d'alimentation du pilote se lit au profil que le pilote
/// Applique au jeu. Le produit lisait le profil de base : un profil de jeu portant sa propre
/// valeur rendait le gate aveugle, dans les deux sens.
/// </summary>
public class SettingsStateReaderDrsScopeTests
{
    private const string Jeu = @"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe";
    private static readonly DrsOriginState Herite = new(DrsValueOrigin.InheritedFromGlobalProfile, false, "");
    private static readonly DrsOriginState Propre = new(DrsValueOrigin.SetByUser, true, "");

    /// <param name="global">Ce que rend le profil de base ; null = non lu.</param>
    /// <param name="auJeu">Ce que rend le profil du jeu ; null = pas de profil propre.</param>
    private sealed class Pilote(uint? global, (uint Valeur, DrsOriginState Origine)? auJeu, bool auDefaut = false) : INvApi
    {
        public bool PreferredPstateAuDefaut() => auDefaut;
        public int LecturesAuJeu { get; private set; }
        public bool IsAvailable => true;

        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
        {
            LecturesAuJeu++;
            return auJeu is { } v
                ? Result<(uint, DrsOriginState)>.Ok((v.Valeur, v.Origine))
                : Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable, "pas de profil propre");
        }

        public Result<uint> GetPreferredPstate() => global is { } g
            ? Result<uint>.Ok(g)
            : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(0);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(0);
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(0);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(0);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private sealed class JeuFixe(string? chemin) : IGameExecutable
    {
        public string? RunningGameExecutablePath() => chemin;
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("Intel Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("RTX 4060", "NVIDIA", 8L << 30, "32", TriState.Unknown) },
        new[] { new MemoryModule("PN", "G.Skill", 3800, 4000) },
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static async Task<SettingsState> Lire(INvApi pilote, IGameExecutable? jeu, IDisplayInfo? ecran = null)
    {
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Ok("381b4222-f694-41f0-9685-ff5bb260df2e"));
        var reg = Substitute.For<IRegistryProvider>();
        reg.GetParkedCoreIndexes().Returns(Array.Empty<int>());
        var net = Substitute.For<INetworkAdapterReader>();
        net.Read().Returns(Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "non lu"));

        var r = await new SettingsStateReader(power, reg, net, pilote, display: ecran, game: jeu).ReadAsync(Hw());
        r.IsOk.Should().BeTrue();
        return r.Value!;
    }

    private static IDisplayInfo EcranUnique144()
    {
        var d = Substitute.For<IDisplayInfo>();
        d.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok(new List<DisplayInfo>
        {
            new(IsPrimaryGameDisplay: true, SupportsVrr: false, RefreshHz: 144, MaxRefreshHz: 144,
                DeviceName: @"\\.\DISPLAY1"),
        }));
        return d;
    }

    [Fact]
    public async Task Le_plafond_d_images_pose_AU_PROFIL_DU_JEU_est_vu_par_le_scan()
    {
        // VrrVsyncCap pose le plafond au profil du jeu
        // depuis `5619aa8`, et le scan le relisait au GLOBAL (0 ici) : le produit ne voyait plus le
        // plafond qu'il avait lui-meme pose, et le classifieur jugeait l'A/B suivant sans lui.
        var etat = await Lire(new Pilote(global: 1, auJeu: (141, Propre)), new JeuFixe(Jeu), EcranUnique144());

        etat.FrameCap.Should().NotBeNull();
        etat.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.Coherent, "141 = 144 - 3, lu au profil du jeu");
        etat.FrameCap.Value.Origin!.Value.Origin.Should().Be(DrsValueOrigin.SetByUser,
            "la provenance vient de la MEME lecture que la valeur");
    }

    [Fact]
    public async Task Un_jeu_qui_HERITE_la_valeur_se_lit_au_jeu_et_dit_d_ou_elle_vient()
    {
        // Mesure du 10/09 sur cod.exe : PREFERRED_PSTATE = 1, herite du global.
        var etat = await Lire(new Pilote(global: 1, auJeu: (1, Herite)), new JeuFixe(Jeu));

        etat.NvidiaPreferMaxPerf.Should().Be(TriState.On);
        etat.NvidiaPreferMaxPerfOrigin!.Value.Origin.Should().Be(DrsValueOrigin.InheritedFromGlobalProfile);
        etat.NvidiaDrsScope.Should().Be(Jeu);
    }

    [Fact]
    public async Task Un_profil_de_jeu_PROPRE_l_emporte_sur_le_global()
    {
        // Le cas que le produit ratait : le global dit « deja au maximum », le jeu porte
        // « Adaptive ». Lire le global fermait le levier sur le seul jeu ou il agirait.
        var etat = await Lire(new Pilote(global: 1, auJeu: (0, Propre)), new JeuFixe(Jeu));

        etat.NvidiaPreferMaxPerf.Should().Be(TriState.Off, "c'est le profil du jeu que le pilote applique");
        etat.NvidiaDrsScope.Should().Be(Jeu);
    }

    [Fact]
    public async Task Sans_jeu_connu_on_lit_le_global_et_le_scope_le_dit()
    {
        var pilote = new Pilote(global: 0, auJeu: (1, Propre));
        var etat = await Lire(pilote, new JeuFixe(null));

        etat.NvidiaPreferMaxPerf.Should().Be(TriState.Off);
        etat.NvidiaDrsScope.Should().BeNull("null = profil global, jamais un jeu devine");
        pilote.LecturesAuJeu.Should().Be(0);
    }

    [Fact]
    public async Task Un_jeu_sans_profil_propre_se_lit_au_global()
    {
        // Sans profil propre, le pilote applique le global : c'est donc lui qui decrit le jeu.
        var etat = await Lire(new Pilote(global: 1, auJeu: null), new JeuFixe(Jeu));

        etat.NvidiaPreferMaxPerf.Should().Be(TriState.On);
        etat.NvidiaDrsScope.Should().BeNull();
    }

    // L'en-tête officiel NvApiDriverSettings.h (github.com/NVIDIA/nvapi) donne six valeurs à
    // PREFERRED_PSTATE, et sa valeur par défaut est OPTIMAL_POWER (5), pas ADAPTIVE (0). Seul 1 (PREFER_MAX) est « au
    // maximum » : toute autre valeur LUE de l'en-tête dit « pas au maximum ». Lues Unknown, 2 à 5 feraient que le levier
    // ne serait jamais proposé sur une carte au réglage par défaut du pilote.
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public async Task Une_valeur_lue_autre_que_le_maximum_dit_pas_au_maximum(uint valeur)
        => (await Lire(new Pilote(global: valeur, auJeu: null), new JeuFixe(null))).NvidiaPreferMaxPerf.Should().Be(TriState.Off);

    [Fact]
    public async Task Une_valeur_hors_de_l_en_tete_reste_Unknown()
        => (await Lire(new Pilote(global: 6, auJeu: null), new JeuFixe(null))).NvidiaPreferMaxPerf.Should().Be(TriState.Unknown);

    [Fact]
    public async Task Rien_de_lisible_reste_Unknown()
    {
        var etat = await Lire(new Pilote(global: null, auJeu: null), new JeuFixe(Jeu));

        etat.NvidiaPreferMaxPerf.Should().Be(TriState.Unknown);
        etat.NvidiaPreferMaxPerfOrigin.Should().BeNull();
    }
    // Absent du profil de base, PREFERRED_PSTATE est au défaut du pilote (OPTIMAL_POWER) : pas au maximum, et lu. Une
    // panne de lecture reste non lue.
    [Fact]
    public async Task Au_defaut_du_pilote_le_mode_de_gestion_n_est_pas_au_maximum()
    {
        (await Lire(new Pilote(global: null, auJeu: null, auDefaut: true), jeu: null)).NvidiaPreferMaxPerf.Should().Be(TriState.Off);
        (await Lire(new Pilote(global: null, auJeu: null), jeu: null)).NvidiaPreferMaxPerf.Should().Be(TriState.Unknown);
    }
}
