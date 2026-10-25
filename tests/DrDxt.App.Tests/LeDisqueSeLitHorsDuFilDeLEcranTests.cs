using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La lecture des disques passe par WMI (MSFT_PhysicalDisk, puis Win32_DiskDrive, avec reprises) : plusieurs secondes sur un
// service WMI froid. Faite sur le fil de l'écran, elle gelait la fenêtre à son ouverture et au début de chaque analyse.
public sealed class LeDisqueSeLitHorsDuFilDeLEcranTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "24.9", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private sealed class DisqueLent : IStorageDetector
    {
        public readonly ManualResetEventSlim Libere = new(false);
        public Result<IReadOnlyList<StorageDevice>> Detect()
        {
            Libere.Wait(TimeSpan.FromSeconds(10));
            return Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) });
        }
    }

    private static ScanViewModel Vm(IStorageDetector disques)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        return new ScanViewModel(
            hd, Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), disques,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), Substitute.For<IDesiredStateStore>(), new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    [Fact]
    public async Task L_ouverture_rend_la_main_avant_que_le_disque_soit_lu_puis_l_affiche()
    {
        var disque = new DisqueLent();
        var vm = Vm(disque);

        // La partie synchrone de l'appel est ce que le fil de l'écran exécute : elle ne doit pas attendre WMI.
        var appel = Task.Factory.StartNew(() => vm.LoadIdleComponentsAsync(), CancellationToken.None,
            TaskCreationOptions.None, TaskScheduler.Default);
        var rendue = await Task.WhenAny(appel, Task.Delay(TimeSpan.FromSeconds(2))) == appel;
        disque.Libere.Set();
        rendue.Should().BeTrue("la lecture du disque ne doit pas tenir le fil appelant");

        await await appel;
        vm.Components.Single(c => c.IconKey == "Ssd").Value.Should().Be("Samsung 990 Pro · NVMe");
    }

    [Fact]
    public async Task Tant_que_le_disque_n_est_pas_lu_il_dit_un_tiret()
    {
        var disque = new DisqueLent();
        var vm = Vm(disque);

        var chargement = vm.LoadIdleComponentsAsync();
        vm.Components.Single(c => c.IconKey == "Ssd").Value.Should().Be("—");

        disque.Libere.Set();
        await chargement;
        vm.Components.Single(c => c.IconKey == "Ssd").Value.Should().Be("Samsung 990 Pro · NVMe");
    }
}
