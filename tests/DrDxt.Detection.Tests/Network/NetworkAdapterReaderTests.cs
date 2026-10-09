using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Detection.Scoring;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Network;

// -----------------------------------------------------------------------
// NS-9 — network adapter read + guidance.
//
// The bugs worth defending against here all LOOK like a working feature:
//   * keying on a localized DisplayName finds nothing on a French Windows and reports
//     "Unknown" everywhere,
//   * RegistryValue is a string[] — ToString() gives "System.String[]", which parses as
//     neither 0 nor 1 and silently becomes Unknown,
//   * an absent keyword read as "Off" would produce advice about a setting the NIC does
//     not even have.
// -----------------------------------------------------------------------
public class NetworkAdapterReaderTests
{
    private const string Ns = @"\\.\root\StandardCimv2";

    private static WmiRow Adapter(string name, string desc, bool virtualAdapter, int connectState, int medium) =>
        new(new Dictionary<string, object?>
        {
            ["Name"] = name,
            ["InterfaceDescription"] = desc,
            ["Virtual"] = virtualAdapter,
            ["MediaConnectState"] = connectState,
            ["ndisPhysicalMedium"] = medium,
        });

    // RegistryValue as CIM really returns it: a string ARRAY.
    private static WmiRow Setting(string adapter, string keyword, string value) =>
        new(new Dictionary<string, object?>
        {
            ["Name"] = adapter,
            ["RegistryKeyword"] = keyword,
            ["RegistryValue"] = new[] { value },
        });

    private static IWmiQuery Wmi(IEnumerable<WmiRow> adapters, IEnumerable<WmiRow>? settings = null)
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Ns, Arg.Is<string>(q => q.Contains("FROM MSFT_NetAdapter ")
                                                  || q.EndsWith("FROM MSFT_NetAdapter")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(adapters.ToList()));
        wmi.QueryInNamespace(Ns, Arg.Is<string>(q => q.Contains("MSFT_NetAdapterAdvancedPropertySettingData")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok((settings ?? Array.Empty<WmiRow>()).ToList()));
        return wmi;
    }

    [Fact]
    public void Reads_the_standardized_ndis_keywords_not_the_localized_display_name()
    {
        var wmi = Wmi(
            new[] { Adapter("Ethernet", "Realtek Gaming 2.5GbE", false, 1, 14) },
            new[]
            {
                Setting("Ethernet", "*InterruptModeration", "1"),
                Setting("Ethernet", "*RSS", "1"),
                Setting("Ethernet", "*EEE", "0"),
            });

        var r = new NetworkAdapterReader(wmi).Read();

        r.IsOk.Should().BeTrue();
        var a = r.Value!.Single();
        a.Name.Should().Be("Ethernet");
        a.IsWired.Should().BeTrue();
        a.InterruptModeration.Should().Be(TriState.On);
        a.Rss.Should().Be(TriState.On);
        a.EnergyEfficientEthernet.Should().Be(TriState.Off);
    }

    [Fact]
    public void An_absent_keyword_is_Unknown_never_Off()
    {
        // A NIC that does not expose *RSS must not be advised about *RSS.
        var wmi = Wmi(
            new[] { Adapter("Ethernet", "Some NIC", false, 1, 14) },
            new[] { Setting("Ethernet", "*InterruptModeration", "0") });

        var a = new NetworkAdapterReader(wmi).Read().Value!.Single();

        a.Rss.Should().Be(TriState.Unknown);
        a.InterruptModeration.Should().Be(TriState.Off);
        NetworkAdvisor.Advise(new[] { a }).Should().BeEmpty("nothing was read that warrants advice");
    }

    [Fact]
    public void A_value_we_do_not_understand_is_Unknown_not_a_guess()
    {
        var wmi = Wmi(
            new[] { Adapter("Ethernet", "Some NIC", false, 1, 14) },
            new[] { Setting("Ethernet", "*InterruptModeration", "3") }); // vendor-wide enum

        new NetworkAdapterReader(wmi).Read().Value!.Single()
            .InterruptModeration.Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Virtual_and_disconnected_adapters_are_excluded()
    {
        var wmi = Wmi(new[]
        {
            Adapter("Ethernet 2", "VirtualBox Host-Only", virtualAdapter: true, connectState: 1, medium: 14),
            Adapter("Local 3", "ExpressVPN TAP", virtualAdapter: true, connectState: 2, medium: 0),
            Adapter("Wi-Fi", "Intel Wi-Fi 6E AX211", virtualAdapter: false, connectState: 2, medium: 9),
            Adapter("Ethernet", "Realtek Gaming 2.5GbE", virtualAdapter: false, connectState: 1, medium: 14),
        });

        var r = new NetworkAdapterReader(wmi).Read();

        r.Value!.Select(a => a.Name).Should().Equal("Ethernet");
    }

    [Fact]
    public void A_connected_wireless_adapter_is_kept_but_marked_not_wired()
    {
        var wmi = Wmi(new[] { Adapter("Wi-Fi", "Intel Wi-Fi 6E AX211", false, 1, 9) });

        var a = new NetworkAdapterReader(wmi).Read().Value!.Single();

        a.IsWired.Should().BeFalse();
    }

    [Fact]
    public void A_failed_settings_query_still_reports_the_adapter_with_unknown_settings()
    {
        // Losing the settings table must not make an adapter disappear — "present, settings
        // unknown" is honest; a silent drop hides hardware the user has.
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Ns, Arg.Is<string>(q => q.EndsWith("FROM MSFT_NetAdapter")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(
               new List<WmiRow> { Adapter("Ethernet", "Realtek", false, 1, 14) }));
        wmi.QueryInNamespace(Ns, Arg.Is<string>(q => q.Contains("AdvancedPropertySettingData")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "denied"));

        var r = new NetworkAdapterReader(wmi).Read();

        r.IsOk.Should().BeTrue();
        var a = r.Value!.Single();
        a.Rss.Should().Be(TriState.Unknown);
        a.InterruptModeration.Should().Be(TriState.Unknown);
    }

    [Fact]
    public void A_failed_adapter_query_fails_clean()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "WMI down"));

        var r = new NetworkAdapterReader(wmi).Read();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }
    // ══════════════════════════════════════════════════════════════════════════════════════
    // `*SelectiveSuspend` — ajoute le 08/09/2026 apres l'avoir trouve actif sur la machine de
    // reference et lu par personne. C'est le seul des trois qui soit normalise (etoile), donc
    // celui qui couvrira le plus de machines.
    // ══════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("EnableGreenEthernet")]
    [InlineData("PowerSavingMode")]
    [InlineData("*SelectiveSuspend")]
    public void CHACUN_DES_TROIS_MOTS_CLES_SUFFIT_A_DIRE_QUE_LE_LIEN_ECONOMISE(string motCle)
    {
        // Une carte n'expose pas les trois. Exiger le trio ferait taire le constat partout ou
        // le constructeur n'utilise qu'un seul nom.
        var lecteur = new NetworkAdapterReader(Wmi(
            new[] { Adapter("Ethernet", "Realtek Gaming 2.5GbE", false, 1, 14) },
            new[] { Setting("Ethernet", motCle, "1") }));

        lecteur.Read().Value!.Single().LinkPowerSaving.Should().Be(TriState.On);
    }

    [Fact]
    public void AUCUN_DES_TROIS_MOTS_CLES_NE_REND_PAS_INACTIF_MAIS_INCONNU()
    {
        // L'asymetrie qui compte. Une carte qui n'expose aucun de ces mots-cles n'est pas une
        // carte sans economie d'energie : c'est une carte dont on ne sait rien. Rendre `Off`
        // ferait dire au produit « rien a signaler » quand il veut dire « je n'ai pas pu lire ».
        var lecteur = new NetworkAdapterReader(Wmi(
            new[] { Adapter("Ethernet", "Carte sans ces reglages", false, 1, 14) },
            new[] { Setting("Ethernet", "*RSS", "1") }));

        lecteur.Read().Value!.Single().LinkPowerSaving.Should().Be(TriState.Unknown);
    }

    [Fact]
    public void UN_SEUL_LU_ET_INACTIF_SUFFIT_A_DIRE_INACTIF()
    {
        // Le controle POSITIF : la lecture doit pouvoir conclure « non » aussi, sinon `Off` ne
        // serait jamais atteignable et le constat serait purement decoratif.
        var lecteur = new NetworkAdapterReader(Wmi(
            new[] { Adapter("Ethernet", "Realtek Gaming 2.5GbE", false, 1, 14) },
            new[] { Setting("Ethernet", "*SelectiveSuspend", "0") }));

        lecteur.Read().Value!.Single().LinkPowerSaving.Should().Be(TriState.Off);
    }
}

public class NetworkAdvisorTests
{
    private static NetworkAdapterState Nic(TriState moderation, TriState rss) =>
        new("Ethernet", "Realtek Gaming 2.5GbE", IsWired: true, moderation, rss, TriState.Off);

    [Fact]
    public void Advises_when_rss_is_off_because_microsoft_wants_it_on()
    {
        var advice = NetworkAdvisor.Advise(new[] { Nic(TriState.Off, TriState.Off) });

        advice.Select(a => a.Id).Should().Contain("net.rss_off");
        advice.Should().OnlyContain(a => a.SourceUrl.StartsWith("https://learn.microsoft.com/"));
    }

    [Fact]
    public void Says_nothing_when_rss_is_already_on()
    {
        NetworkAdvisor.Advise(new[] { Nic(TriState.Off, TriState.On) })
            .Select(a => a.Id).Should().NotContain("net.rss_off");
    }

    [Fact]
    public void Surfaces_interrupt_moderation_as_a_tradeoff_never_as_a_fault()
    {
        var advice = NetworkAdvisor.Advise(new[] { Nic(TriState.On, TriState.On) }).Single();

        advice.Id.Should().Be("net.interrupt_moderation_on");
        // The wording key must not promise a fix — Microsoft NIC tuning guide: marginal, NIC-dependent,
        // costs CPU. It is a trade-off to measure, which NS-9 can now do.
        advice.MessageKey.Should().Be("Finding.Network.InterruptModerationTradeoff");
    }

    [Fact]
    public void Every_piece_of_advice_is_guidance_only_and_carries_its_source()
    {
        var advice = NetworkAdvisor.Advise(new[] { Nic(TriState.On, TriState.Off) });

        advice.Should().HaveCount(2);
        // Microsoft NIC tuning guide: NIC settings are guidance, never auto-applied. There is no
        // apply path in this namespace at all — this asserts the intent stays explicit.
        advice.Should().OnlyContain(a => a.IsGuidanceOnly);
        advice.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.SourceUrl));
    }

    [Fact]
    public void Unknown_settings_produce_silence()
    {
        NetworkAdvisor.Advise(new[] { Nic(TriState.Unknown, TriState.Unknown) }).Should().BeEmpty();
    }
}

public class NetworkFindingsInScanTests
{
    private static HardwareInventory Machine() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "610.88", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static SettingsState State(IReadOnlyList<NetworkAdapterState>? nics) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        NetworkAdapters: nics);

    private static NetworkAdapterState Nic(TriState moderation, TriState rss) =>
        new("Ethernet", "Realtek Gaming 2.5GbE", IsWired: true, moderation, rss, TriState.Off);

    [Fact]
    public void Network_guidance_reaches_the_scan_findings()
    {
        var findings = ScoringRules.Evaluate(Machine(), State(new[] { Nic(TriState.On, TriState.Off) }));

        findings.Should().Contain(f => f.TitleKey == "Finding.Network.RssOff" && f.Severity == Severity.Minor);
        findings.Should().Contain(f => f.TitleKey == "Finding.Network.InterruptModerationTradeoff"
                                    && f.Severity == Severity.Info);
    }

    [Fact]
    public void Network_findings_never_invent_a_gain_and_are_never_paid()
    {
        var net = ScoringRules.Evaluate(Machine(), State(new[] { Nic(TriState.On, TriState.Off) }))
                              .Where(f => f.TitleKey.StartsWith("Finding.Network.")).ToList();

        net.Should().HaveCount(2);
        net.Should().OnlyContain(f => f.EstimatedGainPct == null);
    }

    [Fact]
    public void Not_read_is_silence_not_an_absence_of_problem()
    {
        // null = la lecture WMI a echoue. « Pas lu » ne doit rien conseiller — ni un conseil
        // fonde sur du vide, ni un « tout va bien » implicite.
        ScoringRules.Evaluate(Machine(), State(null))
            .Should().NotContain(f => f.TitleKey.StartsWith("Finding.Network."));
    }

    [Fact]
    public void A_healthy_adapter_produces_no_network_finding_except_the_tradeoff()
    {
        var net = ScoringRules.Evaluate(Machine(), State(new[] { Nic(TriState.Off, TriState.On) }))
                              .Where(f => f.TitleKey.StartsWith("Finding.Network.")).ToList();

        net.Should().BeEmpty("RSS deja ON et moderation deja OFF : rien a dire");
    }

    /// <summary>Les passerelles publiees par toute la machine — la lecture qui dit s'il y a
    /// ambiguite sur le premier saut. Elle compte les sorties DISTINCTES : deux cartes derriere
    /// la meme box ne font pas deux chemins, et une adresse IPv6 n'est pas une cible pour la
    /// sonde IPv4 du produit.</summary>
    [Fact]
    public void Les_passerelles_sont_comptees_DISTINCTES_et_en_IPv4()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.Query(Arg.Is<string>(q => q.Contains("Win32_NetworkAdapterConfiguration")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
           {
               new(new Dictionary<string, object?> { ["DefaultIPGateway"] = new[] { "192.168.1.254" } }),
               // La meme box, vue par une seconde carte : une seule sortie, pas deux.
               new(new Dictionary<string, object?> { ["DefaultIPGateway"] = new[] { "192.168.1.254" } }),
               // Un tunnel monte : une vraie seconde sortie.
               new(new Dictionary<string, object?> { ["DefaultIPGateway"] = new[] { "fe80::1", "10.8.0.1" } }),
               // Une carte adressee sans passerelle : elle ne mene nulle part.
               new(new Dictionary<string, object?>()),
           }));

        var lues = new NetworkAdapterReader(wmi).ReadAllDefaultGateways();

        lues.IsOk.Should().BeTrue();
        lues.Value.Should().Equal("192.168.1.254", "10.8.0.1");
    }
}
