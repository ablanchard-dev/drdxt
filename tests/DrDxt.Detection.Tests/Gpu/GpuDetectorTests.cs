using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Wmi;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class GpuDetectorTests
{
    private static (IDxgiProvider, IAdlxProvider, IWmiQuery, INvReBarProvider, INvmlNative) Mocks(
        int vendorId, string desc, long vram,
        TriState amdSam = TriState.Off, TriState nvReBar = TriState.Unknown,
        string? nvmlBranch = null)
    {
        var dxgi = Substitute.For<IDxgiProvider>();
        dxgi.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Ok(
            new List<DxgiAdapter> { new(desc, vendorId, vram) }));
        var adlx = Substitute.For<IAdlxProvider>();
        adlx.GetSmartAccessMemoryState().Returns(amdSam);
        var nv = Substitute.For<INvReBarProvider>();
        nv.GetReBarState().Returns(nvReBar);
        var wmi = Substitute.For<IWmiQuery>();
        wmi.Query(Arg.Is<string>(s => s.Contains("Win32_VideoController")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
           { new(new Dictionary<string, object?> { ["DriverVersion"] = "31.0.15.4633", ["Name"] = desc }) }));
        var nvml = Substitute.For<INvmlNative>();
        // Par défaut NVML ne rend rien : le détecteur doit alors garder la valeur WMI. C'est le
        // cas d'une machine AMD/Intel, ou d'un pilote NVIDIA trop vieux pour exporter l'appel.
        nvml.TryGetDriverVersion(out Arg.Any<string>())
            .Returns(call => { call[0] = nvmlBranch ?? string.Empty; return nvmlBranch is not null; });
        return (dxgi, adlx, wmi, nv, nvml);
    }

    [Fact]
    public void Nvidia_rebar_comes_from_nvml_provider()
    {
        var (d, a, w, n, m) = Mocks(0x10DE, "NVIDIA GeForce RTX 4070", 12L << 30, nvReBar: TriState.On);
        var r = new GpuDetector(d, a, w, n, m).Detect();
        r.IsOk.Should().BeTrue();
        var gpu = r.Value!.Single();
        gpu.Vendor.Should().Be("NVIDIA");
        gpu.ReBar.Should().Be(TriState.On);
        gpu.VramBytes.Should().Be(12L << 30);
    }

    [Fact]
    public void Nvidia_rebar_unknown_when_nvml_unavailable()
    {
        var (d, a, w, n, m) = Mocks(0x10DE, "NVIDIA GeForce RTX 4060", 8L << 30, nvReBar: TriState.Unknown);
        var gpu = new GpuDetector(d, a, w, n, m).Detect().Value!.Single();
        gpu.Vendor.Should().Be("NVIDIA");
        gpu.ReBar.Should().Be(TriState.Unknown);
    }

    // Une carte d'avant les RTX 30 n'a pas de Resizable BAR : sa fenêtre BAR1 de 256 Mio n'est pas un réglage coupé, et
    // le BIOS n'y peut rien.
    [Theory]
    [InlineData("NVIDIA GeForce GTX 1080", TriState.Unknown)]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", TriState.Unknown)]
    [InlineData("NVIDIA GeForce RTX 3060", TriState.Off)]
    [InlineData("NVIDIA GeForce RTX 4060", TriState.Off)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", TriState.Off)]
    public void Nvidia_rebar_off_only_on_a_card_that_has_it(string nom, TriState attendu)
    {
        var (d, a, w, n, m) = Mocks(0x10DE, nom, 8L << 30, nvReBar: TriState.Off);
        new GpuDetector(d, a, w, n, m).Detect().Value!.Single().ReBar.Should().Be(attendu);
    }

    [Fact]
    public void Amd_gpu_rebar_comes_from_adlx()
    {
        var (d, a, w, n, m) = Mocks(0x1002, "AMD Radeon RX 7800 XT", 16L << 30, amdSam: TriState.On);
        var gpu = new GpuDetector(d, a, w, n, m).Detect().Value!.Single();
        gpu.Vendor.Should().Be("AMD");
        gpu.ReBar.Should().Be(TriState.On);
    }

    [Fact]
    public void Driver_version_filled_from_wmi()
    {
        var (d, a, w, n, m) = Mocks(0x10DE, "NVIDIA GeForce RTX 4070", 12L << 30);
        new GpuDetector(d, a, w, n, m).Detect().Value!.Single().DriverVersion.Should().Be("31.0.15.4633");
    }

    // --- Branche pilote NVIDIA : NVML plutôt que la quadruplette WDDM de WMI. ---

    [Fact]
    public void Nvidia_driver_version_is_the_real_branch_from_nvml_not_the_wddm_quad()
    {
        // WMI rend "31.0.15.4633" (quadruplette WDDM) : le levier de fraîcheur refuse de juger cette
        // forme, donc le levier « pilote obsolète » ne pouvait jamais se déclencher. NVML rend la
        // vraie branche, celle que l'utilisateur reconnaît et qui porte le signal de fraîcheur.
        var (d, a, w, n, m) = Mocks(0x10DE, "NVIDIA GeForce RTX 4060", 8L << 30, nvmlBranch: "610.88");

        new GpuDetector(d, a, w, n, m).Detect().Value!.Single()
            .DriverVersion.Should().Be("610.88");
    }

    [Fact]
    public void Falls_back_to_the_wmi_version_when_nvml_cannot_read()
    {
        // Pilote trop vieux pour exporter l'appel, NVML absent : on garde ce que WMI donne.
        // Jamais de version fabriquée, et surtout pas de champ vide.
        var (d, a, w, n, m) = Mocks(0x10DE, "NVIDIA GeForce GTX 1060", 6L << 30, nvmlBranch: null);

        new GpuDetector(d, a, w, n, m).Detect().Value!.Single()
            .DriverVersion.Should().Be("31.0.15.4633");
    }

    [Fact]
    public void Nvml_is_not_even_asked_on_a_machine_without_an_nvidia_card()
    {
        // Sans ça, une machine AMD ou Intel pure déclenche un chargement de nvml.dll pour un
        // résultat que personne n'utilisera. Rattrapé par la revue d'ensemble.
        var (d, a, w, n, m) = Mocks(0x1002, "AMD Radeon RX 7800 XT", 16L << 30, nvmlBranch: "610.88");

        new GpuDetector(d, a, w, n, m).Detect();

        m.DidNotReceive().TryGetDriverVersion(out Arg.Any<string>());
    }

    [Theory]
    [InlineData(0x1002, "AMD Radeon RX 7800 XT")]
    [InlineData(0x8086, "Intel(R) Arc(TM) A770 Graphics")]
    public void The_nvidia_branch_is_never_stuck_onto_a_non_nvidia_card(int vendorId, string desc)
    {
        // Le test qui mord : NVML est system-wide et répond même sur une machine qui a aussi une
        // carte AMD ou Intel. Coller "610.88" sur une Radeon donnerait un numéro crédible, faux,
        // et jugé contre le mauvais plancher de fraîcheur.
        var (d, a, w, n, m) = Mocks(vendorId, desc, 16L << 30, nvmlBranch: "610.88");

        new GpuDetector(d, a, w, n, m).Detect().Value!.Single()
            .DriverVersion.Should().Be("31.0.15.4633");
    }

    [Fact]
    public void Dxgi_failure_propagates()
    {
        var (_, a, w, n, m) = Mocks(0x10DE, "x", 0);
        var dxgi = Substitute.For<IDxgiProvider>();
        dxgi.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Fail(ErrorCodes.DxgiFail, "x"));
        var r = new GpuDetector(dxgi, a, w, n, m).Detect();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.DxgiFail);
    }

    [Fact]
    public void Intel_igpu_rebar_unknown()
    {
        // iGPU Intel : ni ADLX ni NVML ne s'appliquent → Unknown même si le fournisseur NVML répondait.
        var (d, a, w, n, m) = Mocks(0x8086, "Intel UHD Graphics 770", 0, nvReBar: TriState.On);
        var gpu = new GpuDetector(d, a, w, n, m).Detect().Value!.Single();
        gpu.Vendor.Should().Be("Intel");
        gpu.ReBar.Should().Be(TriState.Unknown);
    }
    // --- Version de pilote par controleur : une machine iGPU + carte dediee a deux lignes WMI. ---

    private static WmiRow Ligne(string name, string pnp, string version) =>
        new(new Dictionary<string, object?> { ["Name"] = name, ["PNPDeviceID"] = pnp, ["DriverVersion"] = version });

    private static readonly WmiRow IntelIgpu =
        Ligne("Intel(R) UHD Graphics 770", @"PCI\VEN_8086&DEV_4680&SUBSYS_7D251462&REV_0C\3&11583659&0&10", "31.0.101.4502");
    private static readonly WmiRow AmdCarte =
        Ligne("AMD Radeon RX 7800 XT", @"PCI\VEN_1002&DEV_747E&SUBSYS_E321148C&REV_C8\6&1A2B3C4D&0&00000019", "31.0.24027.1012");

    [Fact]
    public void Machine_mixte_chaque_carte_recoit_son_propre_pilote()
    {
        var dxgi = Substitute.For<IDxgiProvider>();
        dxgi.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Ok(new List<DxgiAdapter>
        {
            new("AMD Radeon RX 7800 XT", 0x1002, 16L << 30),
            new("Intel(R) UHD Graphics 770", 0x8086, 128L << 20),
        }));
        var wmi = Substitute.For<IWmiQuery>();
        // L'iGPU en premier : c'est l'ordre qui collait son pilote sur la carte AMD.
        wmi.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow> { IntelIgpu, AmdCarte }));

        var gpus = new GpuDetector(dxgi, Substitute.For<IAdlxProvider>(), wmi,
            Substitute.For<INvReBarProvider>(), Substitute.For<INvmlNative>()).Detect().Value!;

        gpus.Single(g => g.Vendor == "AMD").DriverVersion.Should().Be("31.0.24027.1012");
        gpus.Single(g => g.Vendor == "Intel").DriverVersion.Should().Be("31.0.101.4502");
    }

    [Fact]
    public void Nom_different_la_ligne_se_retrouve_par_le_fabricant()
    {
        var carte = new DxgiAdapter("AMD Radeon RX 7800 XT (renomme)", 0x1002, 16L << 30);
        GpuDetector.DriverVersionFor(carte, [IntelIgpu, AmdCarte]).Should().Be("31.0.24027.1012");
    }

    [Fact]
    public void Aucune_ligne_identifiable_rend_Unknown_pas_le_pilote_d_un_autre()
    {
        var carte = new DxgiAdapter("Carte inconnue", 0x10DE, 8L << 30);
        GpuDetector.DriverVersionFor(carte, [IntelIgpu, AmdCarte]).Should().Be("Unknown");
    }

    [Fact]
    public void Seule_ligne_de_la_machine_c_est_la_sienne_meme_sous_un_autre_nom()
    {
        var carte = new DxgiAdapter("Microsoft Basic Render Driver", 0x1414, 0);
        GpuDetector.DriverVersionFor(carte, [AmdCarte]).Should().Be("31.0.24027.1012");
    }
    [Fact]
    public void Deux_cartes_NVIDIA_le_ReBAR_du_device_0_n_est_colle_sur_aucune()
    {
        var dxgi = Substitute.For<IDxgiProvider>();
        dxgi.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Ok(new List<DxgiAdapter>
        {
            new("NVIDIA GeForce RTX 4090", 0x10DE, 24L << 30),
            new("NVIDIA GeForce RTX 3060", 0x10DE, 12L << 30),
        }));
        var nv = Substitute.For<INvReBarProvider>();
        nv.GetReBarState().Returns(TriState.On);
        var wmi = Substitute.For<IWmiQuery>();
        wmi.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>()));

        var gpus = new GpuDetector(dxgi, Substitute.For<IAdlxProvider>(), wmi, nv,
            Substitute.For<INvmlNative>()).Detect().Value!;

        gpus.Should().OnlyContain(g => g.ReBar == TriState.Unknown);
    }
}
