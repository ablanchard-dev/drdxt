using System.IO;
using DrDxt.Core;
using DrDxt.Infrastructure.Diagnostics;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

public sealed class DiagnosticsLogTests : IDisposable
{
    private readonly string _tempDir;

    public DiagnosticsLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // --- acceptation de base : un changement journalisé se relit ---
    [Fact]
    public async Task Change_ThenExport_FileExistsAndContainsAllFields()
    {
        var log = new DiagnosticsLog(_tempDir);

        log.Change("ReflexLowLatency", "Off", "On", "DrDXT");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();

        var exportPath = result.Value!;
        File.Exists(exportPath).Should().BeTrue("export file must exist at the returned path");

        var content = await File.ReadAllTextAsync(exportPath);
        content.Should().Contain("ReflexLowLatency", "tweakId must appear in the export");
        content.Should().Contain("Off",              "from-value must appear in the export");
        content.Should().Contain("On",               "to-value must appear in the export");
        content.Should().Contain("DrDXT",            "actor must appear in the export");
    }

    // --- l'acteur « Windows » est enregistré et se distingue de « DrDXT » ---
    [Fact]
    public async Task Change_WithWindowsActor_RecordedInExport()
    {
        var log = new DiagnosticsLog(_tempDir);

        log.Change("PowerPlan", "Balanced", "HighPerformance", "Windows");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.Value!);
        content.Should().Contain("Windows");
        content.Should().Contain("PowerPlan");
        // le champ acteur est distinct : « Windows » et « DrDXT » sont deux chaînes différentes
        content.Should().NotContain("DrDXT");
    }

    // --- plusieurs appels Change → toutes les entrées présentes, dans l'ordre ---
    [Fact]
    public async Task MultipleChanges_AllEntriesPresentInOrder()
    {
        var log = new DiagnosticsLog(_tempDir);

        log.Change("TweakA", "0", "1", "DrDXT");
        log.Change("TweakB", "old", "new", "Windows");
        log.Change("TweakC", "false", "true", "DrDXT");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.Value!);

        var idxA = content.IndexOf("TweakA", StringComparison.Ordinal);
        var idxB = content.IndexOf("TweakB", StringComparison.Ordinal);
        var idxC = content.IndexOf("TweakC", StringComparison.Ordinal);

        idxA.Should().BeGreaterThanOrEqualTo(0, "TweakA must appear");
        idxB.Should().BeGreaterThan(idxA, "TweakB must appear after TweakA");
        idxC.Should().BeGreaterThan(idxB, "TweakC must appear after TweakB");
    }

    // --- chaque entrée porte un horodatage UTC ---
    [Fact]
    public async Task Change_EntryContainsUtcTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var log = new DiagnosticsLog(_tempDir);

        log.Change("NvidiaTweakX", "Off", "On", "DrDXT");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue();
        var content = await File.ReadAllTextAsync(result.Value!);

        // Le format est la partie date d'ISO 8601 : au minimum l'année est présente.
        // On cherche la date UTC (par exemple « 2026-06- ») comme repère fiable.
        var datePrefix = before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        content.Should().Contain(datePrefix, "entry must contain a UTC date stamp");

        // Vérifier aussi le suffixe « Z » qui marque l'UTC.
        content.Should().Contain("Z", "timestamp must be UTC (ends with Z)");
    }

    // --- des appels Change concurrents sont sérialisés par le verrou → aucune entrée perdue ni doublée ---
    [Fact]
    public async Task ConcurrentChanges_AllEntriesPresent()
    {
        var log = new DiagnosticsLog(_tempDir);
        const int count = 50;

        await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            Task.Run(() => log.Change($"Tweak{i}", "0", "1", "DrDXT"))));

        var result = await log.ExportAsync();
        result.IsOk.Should().BeTrue();

        var content = await File.ReadAllTextAsync(result.Value!);
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(count, "every concurrent Change must produce exactly one journal line");
    }

    // --- ExportAsync sans changement préalable → toujours IsOk, fichier valide ---
    [Fact]
    public async Task ExportAsync_NoPriorChanges_IsOkWithValidFile()
    {
        var log = new DiagnosticsLog(_tempDir);

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue("export of an empty journal must not fail");
        result.Value.Should().NotBeNullOrEmpty();
        File.Exists(result.Value!).Should().BeTrue("export file must exist even for an empty journal");
    }

    // --- le chemin d'export est un chemin absolu ---
    [Fact]
    public async Task ExportAsync_ReturnsAbsolutePath()
    {
        var log = new DiagnosticsLog(_tempDir);
        log.Change("SomeTweak", "a", "b", "DrDXT");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue();
        Path.IsPathRooted(result.Value!).Should().BeTrue("returned path must be absolute");
    }
}
