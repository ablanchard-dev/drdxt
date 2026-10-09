using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

public class StorageDetectorTests
{
    private static IWmiQuery MakeMsft(params (string model, int mediaType, int busType, ulong sizeBytes)[] disks)
    {
        var wmi = Substitute.For<IWmiQuery>();
        var rows = disks.Select(d => new WmiRow(new Dictionary<string, object?>
        {
            ["FriendlyName"] = d.model,
            ["MediaType"] = d.mediaType,
            ["BusType"] = d.busType,
            ["Size"] = d.sizeBytes
        })).ToList();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(rows));
        wmi.Query(Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>()));
        return wmi;
    }

    private static IWmiQuery MsftFails_Win32Returns(params (string model, ulong sizeBytes)[] disks)
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "namespace unavailable"));
        var rows = disks.Select(d => new WmiRow(new Dictionary<string, object?>
        {
            ["Model"] = d.model,
            ["Size"] = d.sizeBytes,
            ["MediaType"] = "Fixed hard disk media"
        })).ToList();
        wmi.Query(Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(rows));
        return wmi;
    }

    // 04/10/2026 (audit à l'écran, plan 5.2) : sur la machine de référence, Windows liste la clé USB (bus 7) avant le
    // SSD NVMe, et l'accueil affichait « Stockage : JetFlash Transcend 32GB ». Les bus amovibles (USB 7, SD 12, MMC 13)
    // passent après les disques internes.
    [Fact]
    public void Une_cle_usb_listee_en_premier_passe_apres_le_disque_interne()
    {
        var wmi = MakeMsft(("JetFlash Transcend 32GB", 0, 7, 32_000_000_000UL), ("Samsung SSD 990 PRO 2TB", 4, 17, 2_000_000_000_000UL));
        new StorageDetector(wmi).Detect().Value!.Select(d => d.Model)
            .Should().Equal("Samsung SSD 990 PRO 2TB", "JetFlash Transcend 32GB");
    }

    // 04/10/2026 : WMI rend BusType et MediaType en entier 16 bits non signé (UInt16), pas en int. GetInt ne le lisait
    // pas : aucun NVMe ni aucune clé USB n'était reconnu sur une vraie machine, les tests passaient avec des int.
    [Fact]
    public void Les_types_reels_de_wmi_en_uint16_sont_lus()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
        {
            new(new Dictionary<string, object?> { ["FriendlyName"] = "JetFlash Transcend 32GB", ["MediaType"] = (ushort)0, ["BusType"] = (ushort)7, ["Size"] = 32_000_000_000UL }),
            new(new Dictionary<string, object?> { ["FriendlyName"] = "Samsung SSD 990 PRO 2TB", ["MediaType"] = (ushort)4, ["BusType"] = (ushort)17, ["Size"] = 2_000_000_000_000UL }),
        }));

        var disques = new StorageDetector(wmi).Detect().Value!;

        disques[0].Should().Be(new DrDxt.Core.StorageDevice("Samsung SSD 990 PRO 2TB", "NVMe", 2_000_000_000_000L));
    }

    [Fact]
    public void NVMe_disk_detected_via_bus_type_17()
    {
        // BusType=17 → NVMe regardless of MediaType
        var wmi = MakeMsft(("Samsung 970 EVO", 4, 17, 1_000_000_000_000UL));
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        var disk = result.Value!.Single();
        disk.MediaKind.Should().Be("NVMe");
        disk.Model.Should().Be("Samsung 970 EVO");
        disk.SizeBytes.Should().Be(1_000_000_000_000L);
    }

    [Fact]
    public void SSD_detected_when_media_type_4_and_not_nvme()
    {
        // MediaType=4, BusType=11 (SATA) → SSD
        var wmi = MakeMsft(("Crucial MX500", 4, 11, 500_000_000_000UL));
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        result.Value!.Single().MediaKind.Should().Be("SSD");
    }

    [Fact]
    public void HDD_detected_when_media_type_3()
    {
        // MediaType=3, BusType=11 (SATA) → HDD
        var wmi = MakeMsft(("WD Blue 2TB", 3, 11, 2_000_000_000_000UL));
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        result.Value!.Single().MediaKind.Should().Be("HDD");
    }

    [Fact]
    public void Multiple_disks_all_returned()
    {
        var wmi = MakeMsft(
            ("NVMe Drive", 4, 17, 500_000_000_000UL),
            ("HDD", 3, 11, 1_000_000_000_000UL));
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
    }

    [Fact]
    public void Falls_back_to_win32_when_msft_namespace_unavailable()
    {
        var wmi = MsftFails_Win32Returns(("WD Blue", 500_107_862_016UL));
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        result.Value!.Single().Model.Should().Be("WD Blue");
        result.Value!.Single().MediaKind.Should().Be("HDD");
    }

    [Fact]
    public void Never_throws_on_null_wmi_fields()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.QueryInNamespace(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
           {
               new(new Dictionary<string, object?> { ["FriendlyName"] = null, ["MediaType"] = null, ["BusType"] = null, ["Size"] = null })
           }));
        wmi.Query(Arg.Any<string>())
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>()));
        var act = () => new StorageDetector(wmi).Detect();
        act.Should().NotThrow();
        var result = new StorageDetector(wmi).Detect();
        result.IsOk.Should().BeTrue();
        result.Value!.Single().MediaKind.Should().Be("Unknown");
    }

    [Fact]
    public void Real_machine_has_at_least_one_storage_device()
    {
        // Light real-machine integration test — no throw, ≥1 device
        var provider = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();
        var detector = provider.GetRequiredService<IStorageDetector>();
        var result = detector.Detect();
        result.IsOk.Should().BeTrue("storage detection should not throw");
        result.Value!.Should().NotBeEmpty("a real machine always has at least one disk");
    }
}
