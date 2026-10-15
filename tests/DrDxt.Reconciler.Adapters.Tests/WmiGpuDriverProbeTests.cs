using DrDxt.Core;
using DrDxt.Detection.Wmi;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// WmiGpuDriverProbe : interroge Win32_VideoController et rend la DriverVersion de l'adaptateur
// qui correspond à un constructeur (sous-chaîne insensible à la casse sur AdapterCompatibility
// ou Name). Aucune correspondance → Ok(null) (absence ≠ erreur) ; requête en échec → Fail.
// -----------------------------------------------------------------------
public sealed class WmiGpuDriverProbeTests
{
    // Un faux IWmiQuery qui rend des lignes toutes faites (ou un échec).
    private sealed class FakeWmi : IWmiQuery
    {
        private readonly Result<IReadOnlyList<WmiRow>> _result;
        public string? LastWql { get; private set; }

        private FakeWmi(Result<IReadOnlyList<WmiRow>> result) => _result = result;

        public static FakeWmi Returning(params WmiRow[] rows)
            => new(Result<IReadOnlyList<WmiRow>>.Ok(rows));

        public static FakeWmi Failing()
            => new(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "wmi failed (test)"));

        public Result<IReadOnlyList<WmiRow>> Query(string wql)
        {
            LastWql = wql;
            return _result;
        }

        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
        {
            LastWql = wql;
            return _result;
        }
    }

    private static WmiRow Row(string? compat, string? name, string? driverVersion)
        => new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AdapterCompatibility"] = compat,
            ["Name"] = name,
            ["DriverVersion"] = driverVersion,
        });

    [Fact]
    public void ReturnsDriverVersion_WhenVendorMatchesAdapterCompatibility()
    {
        var wmi = FakeWmi.Returning(
            Row("NVIDIA", "NVIDIA GeForce RTX 4080", "31.0.15.5161"));
        var probe = new WmiGpuDriverProbe(wmi);

        var r = probe.ReadDriverVersion("nvidia");

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("31.0.15.5161");
    }

    [Fact]
    public void MatchesOnName_WhenAdapterCompatibilityIsAbsent()
    {
        var wmi = FakeWmi.Returning(
            Row(compat: null, name: "NVIDIA GeForce RTX 4080", driverVersion: "31.0.15.5161"));
        var probe = new WmiGpuDriverProbe(wmi);

        var r = probe.ReadDriverVersion("NVIDIA");

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("31.0.15.5161");
    }

    [Fact]
    public void ReturnsOkNull_WhenNoAdapterMatchesTheVendor()
    {
        var wmi = FakeWmi.Returning(
            Row("NVIDIA", "NVIDIA GeForce RTX 4080", "31.0.15.5161"));
        var probe = new WmiGpuDriverProbe(wmi);

        var r = probe.ReadDriverVersion("AMD");

        r.IsOk.Should().BeTrue();   // l'absence n'est pas une erreur
        r.Value.Should().BeNull();
    }

    [Fact]
    public void MultiGpu_ReturnsTheMatchingAdaptersDriver()
    {
        var wmi = FakeWmi.Returning(
            Row("Intel Corporation", "Intel UHD Graphics 770", "31.0.101.4502"),
            Row("Advanced Micro Devices, Inc.", "AMD Radeon RX 7900 XTX", "24.8.1"),
            Row("NVIDIA", "NVIDIA GeForce RTX 4080", "31.0.15.5161"));
        var probe = new WmiGpuDriverProbe(wmi);

        probe.ReadDriverVersion("amd").Value.Should().Be("24.8.1");
        probe.ReadDriverVersion("nvidia").Value.Should().Be("31.0.15.5161");
        probe.ReadDriverVersion("intel").Value.Should().Be("31.0.101.4502");
    }

    [Fact]
    public void QueryFailure_PropagatesFail()
    {
        var probe = new WmiGpuDriverProbe(FakeWmi.Failing());

        var r = probe.ReadDriverVersion("NVIDIA");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }

    [Fact]
    public void MatchingAdapterWithBlankDriverVersion_ReturnsOkNull()
    {
        var wmi = FakeWmi.Returning(Row("NVIDIA", "NVIDIA GeForce RTX 4080", driverVersion: "  "));
        var probe = new WmiGpuDriverProbe(wmi);

        var r = probe.ReadDriverVersion("NVIDIA");

        r.IsOk.Should().BeTrue();
        r.Value.Should().BeNull();
    }

    [Fact]
    public void QueriesWin32VideoController()
    {
        var wmi = FakeWmi.Returning(Row("NVIDIA", "RTX", "1.0"));
        var probe = new WmiGpuDriverProbe(wmi);

        probe.ReadDriverVersion("NVIDIA");

        wmi.LastWql.Should().Contain("Win32_VideoController");
    }
}
