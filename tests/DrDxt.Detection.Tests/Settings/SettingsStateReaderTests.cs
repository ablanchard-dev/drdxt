using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Detection.Settings;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

public class SettingsStateReaderTests
{

    // Sonde reseau NEUTRE : la lecture echoue => SettingsState.NetworkAdapters reste null =>
    // aucun conseil reseau. Ces tests parlent d'autre chose, le reseau ne doit pas les polluer.
    private static INetworkAdapterReader NoNetwork()
    {
        var r = Substitute.For<INetworkAdapterReader>();
        r.Read().Returns(Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "non lu"));
        return r;
    }

    private static HardwareInventory Hw(TriState gpuReBar, params MemoryModule[] mem) => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("RTX 4070", "NVIDIA", 12L<<30, "31", gpuReBar) },
        mem.Length == 0 ? new[] { new MemoryModule("PN", "Corsair", 4800, 6000) } : mem,
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1.A0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static (IPowerProvider, IRegistryProvider) Providers(
        string powerGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
        TriState hags = TriState.On, TriState gameMode = TriState.On)
    {
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Ok(powerGuid));
        var reg = Substitute.For<IRegistryProvider>();
        reg.GetHags().Returns(hags);
        reg.GetGameMode().Returns(gameMode);
        reg.GetParkedCoreIndexes().Returns(Array.Empty<int>());
        reg.GetVrr().Returns(TriState.Unknown);
        return (power, reg);
    }

    // ---- cap d'images : le verdict doit ATTEINDRE l'utilisateur ----------------------
    // FrameCapInferencer existait, teste et mute-teste, et personne dans le produit ne
    // l'appelait : le verdict ne vivait que dans une sonde en ligne de commande. Un
    // diagnostic que le produit ne prononce pas ne sert a rien -- c'est exactement le
    // reproche fait au code qui lisait 141 depuis des mois sans rien en dire.

    private static IDisplayInfo Screens(params (int Hz, bool Primary)[] screens)
    {
        var d = Substitute.For<IDisplayInfo>();
        var list = screens.Select(x => new DisplayInfo(
            IsPrimaryGameDisplay: x.Primary, SupportsVrr: false, RefreshHz: x.Hz,
            MaxRefreshHz: x.Hz, DeviceName: $@"\.\DISPLAY{(x.Primary ? 1 : 2)}")).ToList();
        d.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok(list));
        return d;
    }

    private static INvApi Driver(uint? frameCap)
    {
        var n = Substitute.For<INvApi>();
        n.GetFrameRateLimit().Returns(frameCap is null
            ? Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu")
            : Result<uint>.Ok(frameCap.Value));
        // Une doublure non configuree rend un Result par defaut : ces tests parlent du verdict,
        // pas de la provenance, donc on la declare explicitement non lue.
        n.GetFrameRateLimitOrigin().Returns(
            Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lue"));
        return n;
    }

    // ---- Le VRR lu au pilote sur l'ecran du jeu ----------------------------

    private static IDisplayInfo EcranVrr(TriState possible, TriState actif)
    {
        var d = Substitute.For<IDisplayInfo>();
        d.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok(new List<DisplayInfo>
        {
            new(IsPrimaryGameDisplay: true, SupportsVrr: possible == TriState.On && actif == TriState.On,
                RefreshHz: 200, MaxRefreshHz: 200, DeviceName: @"\\.\DISPLAY1",
                VrrPossible: possible, VrrEnabled: actif),
        }));
        return d;
    }

    [Theory]
    [InlineData(TriState.On, TriState.Off)]
    [InlineData(TriState.Off, TriState.Off)]
    [InlineData(TriState.On, TriState.On)]
    public async Task Le_VRR_lu_au_pilote_arrive_dans_l_etat(TriState possible, TriState actif)
    {
        var (p, r) = Providers();   // le registre dit Unknown : c'est bien le pilote qui parle
        var st = (await new SettingsStateReader(p, r, NoNetwork(), Driver(null), EcranVrr(possible, actif))
            .ReadAsync(Hw(TriState.Unknown))).Value!;

        st.VrrCapableDisplay.Should().Be(possible);
        st.Vrr.Should().Be(actif);
    }

    [Theory]
    [InlineData(1u, TriState.On)]    // plein ecran seulement : active, meme si rien ne tourne au bureau
    [InlineData(2u, TriState.On)]
    [InlineData(0u, TriState.Off)]
    public async Task Le_reglage_G_SYNC_du_pilote_fait_foi_sur_l_etat_instantane(uint mode, TriState attendu)
    {
        // Releve sur la machine de reference le 25/09/2026 : VRR_MODE = 1, et NVAPI « actif = Off » au bureau.
        var (p, r) = Providers();
        var pilote = Driver(null);
        pilote.GetGsyncMode().Returns(Result<uint>.Ok(mode));
        var st = (await new SettingsStateReader(p, r, NoNetwork(), pilote, EcranVrr(TriState.On, TriState.Off))
            .ReadAsync(Hw(TriState.Unknown))).Value!;

        st.Vrr.Should().Be(attendu);
    }

    [Fact]
    public async Task VRR_non_lu_au_pilote_retombe_sur_le_registre_jamais_sur_Off()
    {
        var (p, r) = Providers();
        var st = (await new SettingsStateReader(p, r, NoNetwork(), Driver(null),
                EcranVrr(TriState.Unknown, TriState.Unknown)).ReadAsync(Hw(TriState.Unknown))).Value!;

        st.Vrr.Should().Be(TriState.Unknown);
        st.VrrCapableDisplay.Should().Be(TriState.Unknown);
    }

    [Fact]
    public async Task Les_frequences_de_TOUS_les_ecrans_arrivent_dans_l_etat()
    {
        // Le defaut que ce test ferme : une liste des frequences calculee puis jamais passee au
        // constructeur de l'etat. Le diagnostic de consommation au repos serait mort, avec l'advisor
        // teste, le bareme cable, et un `null` au milieu : le lecteur d'ecrans rendrait 200 et 144 Hz
        // et l'etat dirait « jamais lu ».
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork(), Driver(null), Screens((200, true), (144, false)));

        var st = await sut.ReadAsync(Hw(TriState.Unknown));

        st.IsOk.Should().BeTrue();
        st.Value!.AttachedRefreshHz.Should().Equal(200, 144);
    }

    [Fact]
    public async Task Sans_lecteur_d_ecrans_les_frequences_restent_NON_LUES()
    {
        // Et surtout pas une liste vide : « pas lu » n'est pas « aucun ecran ».
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork(), Driver(null), display: null);

        var st = await sut.ReadAsync(Hw(TriState.Unknown));

        st.Value!.AttachedRefreshHz.Should().BeNull();
    }

    [Fact]
    public async Task Says_when_the_frame_cap_was_computed_on_another_display()
    {
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork(),
            Driver(141), Screens((200, true), (144, false)));

        var state = await sut.ReadAsync(Hw(TriState.Unknown));

        state.Value!.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.ComputedOnAnotherDisplay);
        state.Value.FrameCap.Value.Reason.Should().Contain("144").And.Contain("200");
    }

    [Fact]
    public async Task Without_a_driver_and_display_seam_no_frame_cap_verdict_is_claimed()
    {
        var (p, r) = Providers();

        var state = await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown));

        state.Value!.FrameCap.Should().BeNull("null veut dire jamais calcule, pas rien a signaler");
    }

    [Fact]
    public async Task An_unreadable_driver_cap_is_indeterminate_not_a_clean_bill_of_health()
    {
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork(),
            Driver(null), Screens((200, true)));

        var state = await sut.ReadAsync(Hw(TriState.Unknown));

        state.Value!.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.Indeterminate);
    }

    [Fact]
    public async Task Infers_xmp_off_when_configured_below_rated()
    {
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork());
        var state = await sut.ReadAsync(Hw(TriState.Unknown));
        state.Value!.XmpExpo.Should().Be(TriState.Off);
    }

    [Fact]
    public async Task Rebar_taken_from_detected_gpu()
    {
        var (p, r) = Providers();
        var sut = new SettingsStateReader(p, r, NoNetwork());
        var state = await sut.ReadAsync(Hw(TriState.Unknown));
        state.Value!.ReBar.Should().Be(TriState.Unknown); // NVIDIA → Unknown propagé
    }

    [Fact]
    public async Task Power_plan_guid_propagated()
    {
        var (p, r) = Providers(powerGuid: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"); // Performances élevées
        var state = await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown));
        state.Value!.PowerPlanGuid.Should().Be("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    }

    [Fact]
    public async Task Power_provider_failure_propagates_as_fail()
    {
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Fail(ErrorCodes.PowerFail, "x"));
        var reg = Substitute.For<IRegistryProvider>();
        var state = await new SettingsStateReader(power, reg, NoNetwork()).ReadAsync(Hw(TriState.Unknown));
        state.IsOk.Should().BeFalse();
        state.Error!.Value.Code.Should().Be(ErrorCodes.PowerFail);
    }

    [Fact]
    public async Task Vrr_propagated_from_registry()
    {
        var (p, r) = Providers();
        r.GetVrr().Returns(TriState.On);
        var state = await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown));
        state.Value!.Vrr.Should().Be(TriState.On);
    }

    [Fact]
    public async Task Vrr_unknown_when_registry_not_readable()
    {
        var (p, r) = Providers();
        r.GetVrr().Returns(TriState.Unknown);
        var state = await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown));
        state.Value!.Vrr.Should().Be(TriState.Unknown, "VRR registry key absent or unreadable → honest Unknown");
    }

    // ── LR — les redemarrages sans arret propre. Trois etats, jamais deux. ──────────────────

    [Fact]
    public async Task A_read_shutdown_list_reaches_the_state()
    {
        var (p, r) = Providers();
        var log = Substitute.For<IUnexpectedShutdownLog>();
        var dates = new[] { DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-9) };
        log.Since(Arg.Any<DateTimeOffset>())
            .Returns(Result<IReadOnlyList<DateTimeOffset>>.Ok(dates));

        var state = await new SettingsStateReader(p, r, NoNetwork(), shutdowns: log)
            .ReadAsync(Hw(TriState.Unknown));

        state.Value!.UncleanShutdowns.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unreadable_log_is_never_read_and_not_an_empty_list()
    {
        // La faute qui rendrait ce produit menteur : traduire « je n'ai pas pu lire le journal »
        // en « aucun redemarrage sale », donc en « rien a signaler ».
        var (p, r) = Providers();
        var log = Substitute.For<IUnexpectedShutdownLog>();
        log.Since(Arg.Any<DateTimeOffset>()).Returns(
            Result<IReadOnlyList<DateTimeOffset>>.Fail(ErrorCodes.DetectionFailed, "journal absent"));

        var state = await new SettingsStateReader(p, r, NoNetwork(), shutdowns: log)
            .ReadAsync(Hw(TriState.Unknown));

        state.Value!.UncleanShutdowns.Should().BeNull();
    }

    [Fact]
    public async Task A_clean_machine_yields_an_empty_list_not_null()
    {
        var (p, r) = Providers();
        var log = Substitute.For<IUnexpectedShutdownLog>();
        log.Since(Arg.Any<DateTimeOffset>())
            .Returns(Result<IReadOnlyList<DateTimeOffset>>.Ok(Array.Empty<DateTimeOffset>()));

        var state = await new SettingsStateReader(p, r, NoNetwork(), shutdowns: log)
            .ReadAsync(Hw(TriState.Unknown));

        state.Value!.UncleanShutdowns.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task Without_a_log_seam_nothing_is_claimed()
    {
        var (p, r) = Providers();

        var state = await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown));

        state.Value!.UncleanShutdowns.Should().BeNull("null veut dire jamais lu");
    }

    // Les heures d'activité lues atteignent l'état ; non lues, elles restent null (jamais « 8 h à 17 h » par défaut).
    [Fact]
    public async Task Les_heures_d_activite_lues_atteignent_l_etat()
    {
        var (p, r) = Providers();
        r.GetHeuresActives().Returns(new HeuresActivesLues(15, 8, null));
        (await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown))).Value!.HeuresActives
            .Should().Be(new HeuresActivesLues(15, 8, null));

        var (p2, r2) = Providers();
        r2.GetHeuresActives().Returns((HeuresActivesLues?)null);
        (await new SettingsStateReader(p2, r2, NoNetwork()).ReadAsync(Hw(TriState.Unknown))).Value!.HeuresActives.Should().BeNull();
    }

    // Hors jeu, l'optimiseur 3D V-Cache ne parque rien : « aucun cœur parqué » y est la norme d'un X3D sain, pas un routage
    // cassé. Le parcage ne se lit que jeu ouvert ; sinon il reste non lu.
    [Fact]
    public async Task Le_parcage_ne_se_lit_que_jeu_ouvert()
    {
        var (p, r) = Providers();
        var jeu = Substitute.For<IGameExecutable>();
        jeu.RunningGameExecutablePath().Returns((string?)null);
        (await new SettingsStateReader(p, r, NoNetwork(), game: jeu).ReadAsync(Hw(TriState.Unknown))).Value!.ParkedCoreIndexes
            .Should().BeNull();
        r.DidNotReceive().GetParkedCoreIndexes();

        // Le profil visé est celui d'un jeu choisi, mais aucun jeu ne tourne : le parcage ne se lit pas.
        jeu.RunningGameExecutablePath().Returns(@"C:\Jeux\JeuA\JeuA.exe");
        jeu.JeuTourne().Returns(false);
        (await new SettingsStateReader(p, r, NoNetwork(), game: jeu).ReadAsync(Hw(TriState.Unknown))).Value!.ParkedCoreIndexes
            .Should().BeNull("un profil visé n'est pas un jeu ouvert");
        r.DidNotReceive().GetParkedCoreIndexes();

        jeu.JeuTourne().Returns(true);
        (await new SettingsStateReader(p, r, NoNetwork(), game: jeu).ReadAsync(Hw(TriState.Unknown))).Value!.ParkedCoreIndexes
            .Should().BeEmpty("témoin : jeu ouvert, le parcage se lit");

        (await new SettingsStateReader(p, r, NoNetwork()).ReadAsync(Hw(TriState.Unknown))).Value!.ParkedCoreIndexes
            .Should().BeNull("sans couture du jeu, rien ne dit qu'un jeu tourne");
    }
}
