using System.Diagnostics;
using System.Text;
using DrDxt.App.Platform;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// L'attente d'un jeu réduit, de bout en bout sur le vrai Windows : le vrai localisateur, une
// vraie fenêtre plein écran réduite dans un processus enfant, qui revient à l'écran au bout de 3 s comme
// un joueur qui retourne dans sa partie. Le scan doit l'attendre, puis mesurer CE processus.
[Collection(CollectionFenetres.Nom)]
public sealed class ScanAttendUnVraiJeuReduitOnThisMachineTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000), new MemoryModule("PN", "Kingston", 3800, 4000) },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    [Fact]
    [Trait("Category", "Machine")] // ouvre une fenetre dans la vraie session : rien sur un runner CI
    public async Task Le_scan_attend_une_vraie_fenetre_reduite_puis_mesure_son_processus()
    {
        var locator = new Win32GameLocator();
        // Affirmée, pas sautée : un test qui se saute ne rougit jamais. garde-jeu refuse toute suite jeu lancé.
        locator.JeuOuvert().Should().BeFalse("précondition : aucun jeu ouvert, sinon il serait attendu à la place de la sonde");

        using var enfant = Process.Start(new ProcessStartInfo(SondeDeJeu.Exe,
            "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            enfant.StandardOutput.ReadLine().Should().Be("reduite");
            locator.JeuOuvert().Should().BeTrue("temoin : la sonde réduite est vue comme un jeu ouvert");
            locator.LocateRunningGamePid().Should().BeNull("temoin : réduite, elle n'est pas à capturer");

            var hd = Substitute.For<IHardwareDetector>();
            hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
            var sr = Substitute.For<ISettingsStateReader>();
            sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
            var br = Substitute.For<IBenchmarkRunner>();
            br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "capture non simulée"));
            var sc = Substitute.For<IScanScorer>();
            sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
              .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), null)));
            var sd = Substitute.For<IStorageDetector>();
            sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
            var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k, gameLocator: locator);

            await vm.ScanCommand.ExecuteAsync(null);

            await br.Received().RunAsync(enfant.Id, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { enfant.Kill(); } catch { /* deja termine */ }
            enfant.WaitForExit(5000);
        }
    }

    // La surveillance de la capture, sur le vrai Windows : la sonde est affichée, puis réduite en pleine
    // capture (le joueur revient sur DrDXT). Le chiffre doit être écarté ; restée affichée, il est gardé.
    [Theory]
    [Trait("Category", "Machine")] // ouvre une fenetre dans la vraie session : rien sur un runner CI
    [InlineData(true)]
    [InlineData(false)]
    public async Task Une_vraie_fenetre_reduite_en_pleine_capture_fait_ecarter_le_chiffre(bool reduiteEnCours)
    {
        var locator = new Win32GameLocator();
        locator.JeuOuvert().Should().BeFalse("précondition : aucun jeu ouvert, sinon il serait mesuré à la place de la sonde");

        using var enfant = Process.Start(new ProcessStartInfo(SondeDeJeu.Exe,
            "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ScriptCapture(reduiteEnCours))))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            enfant.StandardOutput.ReadLine().Should().Be("affichee");
            locator.LocateRunningGamePid().Should().Be(enfant.Id, "temoin : la sonde affichée est le jeu à capturer");

            var hd = Substitute.For<IHardwareDetector>();
            hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
            var sr = Substitute.For<ISettingsStateReader>();
            sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
            var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
            var br = Substitute.For<IBenchmarkRunner>();
            // Une capture qui dure, comme la vraie : la surveillance tourne pendant ce temps.
            br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(async _ => { await Task.Delay(4000); return Result<BenchmarkResult>.Ok(bl); });
            var sc = Substitute.For<IScanScorer>();
            sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
              .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), bl)));
            var sd = Substitute.For<IStorageDetector>();
            sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
            var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k, gameLocator: locator);

            await vm.ScanCommand.ExecuteAsync(null);

            await br.Received().RunAsync(enfant.Id, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            // Un vrai processus a un nom, la note le dit.
            if (reduiteEnCours) vm.DegradedNote.Should().Be("Scan.Degraded.JeuQuitteNomme");
            else (vm.DegradedNote ?? "").Should().NotStartWith("Scan.Degraded.JeuQuitte");
        }
        finally
        {
            try { enfant.Kill(); } catch { /* deja termine */ }
            enfant.WaitForExit(5000);
        }
    }

    private static string ScriptCapture(bool reduire) => $$"""
        Add-Type -AssemblyName System.Windows.Forms
        $f = New-Object System.Windows.Forms.Form
        $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.Opacity = 0.01; $f.ShowInTaskbar = $false
        $f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $f.Show(); [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
        [Console]::Out.WriteLine('affichee'); [Console]::Out.Flush()
        $moment = (Get-Date).AddSeconds(2)
        while ((Get-Date) -lt $moment) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        if (${{(reduire ? "true" : "false")}}) { $f.WindowState = 'Minimized' }
        while ($true) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        """;

    private const string Script = """
        Add-Type -AssemblyName System.Windows.Forms
        $f = New-Object System.Windows.Forms.Form
        $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.Opacity = 0.01; $f.ShowInTaskbar = $false
        $f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $f.Show(); [System.Windows.Forms.Application]::DoEvents()
        $f.WindowState = 'Minimized'; [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
        [Console]::Out.WriteLine('reduite'); [Console]::Out.Flush()
        $retour = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $retour) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        $f.WindowState = 'Normal'
        while ($true) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        """;
}
