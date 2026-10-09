using DrDxt.Core;
using DrDxt.Infrastructure.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;

namespace DrDxt.Infrastructure.Tests;

public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempDiagDir;
    private readonly string _tempLogDir;

    public ServiceCollectionExtensionsTests()
    {
        _tempDiagDir = Path.Combine(Path.GetTempPath(), "drdxt-test-diag-" + Guid.NewGuid());
        _tempLogDir  = Path.Combine(Path.GetTempPath(), "drdxt-test-log-"  + Guid.NewGuid());
        Directory.CreateDirectory(_tempDiagDir);
        Directory.CreateDirectory(_tempLogDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDiagDir)) Directory.Delete(_tempDiagDir, recursive: true);
        if (Directory.Exists(_tempLogDir))  Directory.Delete(_tempLogDir,  recursive: true);
    }

    [Fact]
    public void AddDrDxtCore_ResolvesIDiagnosticsLog()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(_tempDiagDir, _tempLogDir);

        using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<IDiagnosticsLog>();

        log.Should().NotBeNull();
        log.Should().BeOfType<DiagnosticsLog>();
    }

    [Fact]
    public void AddDrDxtCore_IDiagnosticsLog_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(_tempDiagDir, _tempLogDir);

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IDiagnosticsLog>();
        var b = provider.GetRequiredService<IDiagnosticsLog>();

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void AddDrDxtCore_ResolvesSerilogILogger()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(_tempDiagDir, _tempLogDir);

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger>();

        logger.Should().NotBeNull();
    }

    [Fact]
    public async Task AddDrDxtCore_DiagnosticsLog_WorksEndToEnd()
    {
        var services = new ServiceCollection();
        services.AddDrDxtCore(_tempDiagDir, _tempLogDir);

        using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<IDiagnosticsLog>();

        log.Change("CPU_Freq", "3200MHz", "3600MHz", "DrDXT");

        var result = await log.ExportAsync();

        result.IsOk.Should().BeTrue("export after a Change call must succeed");
        result.Value.Should().NotBeNullOrEmpty();
        File.Exists(result.Value!).Should().BeTrue("export file must exist");

        var content = await File.ReadAllTextAsync(result.Value!);
        content.Should().Contain("CPU_Freq");
        content.Should().Contain("3600MHz");
    }

    [Fact]
    public void DI_Pattern_FakeImplementations_Resolve()
    {
        var fakeHardware  = Substitute.For<IHardwareDetector>();
        var fakeBenchmark = Substitute.For<IBenchmarkRunner>();
        var fakeTweak     = Substitute.For<ITweakEngine>();

        var services = new ServiceCollection();
        services.AddSingleton(fakeHardware);
        services.AddSingleton(fakeBenchmark);
        services.AddSingleton(fakeTweak);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHardwareDetector>().Should().BeSameAs(fakeHardware);
        provider.GetRequiredService<IBenchmarkRunner>().Should().BeSameAs(fakeBenchmark);
        provider.GetRequiredService<ITweakEngine>().Should().BeSameAs(fakeTweak);
    }
}
