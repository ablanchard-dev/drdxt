using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// R2 (SAFE half) — NvApiInfo: read GPU name + architecture, fail-clean if no NVIDIA.
//
// Read-only, unelevated-safe. Filters to NVIDIA GPUs, derives the architecture from
// the marketing name (the verified, fail-clean path) so a later gate can disable
// voltage-undervolt on Blackwell.
// -----------------------------------------------------------------------
public sealed class NvApiInfoTests
{
    private static GpuInfo Gpu(string model, string vendor) =>
        new(model, vendor, 12L << 30, "551.23", TriState.On);

    [Fact]
    public void Reads_name_and_blackwell_architecture_for_nvidia_gpu()
    {
        var info = new NvApiInfo(new[] { Gpu("NVIDIA GeForce RTX 5080", "NVIDIA") });

        info.IsAvailable.Should().BeTrue();
        var id = info.GetGpuIdentity(0);
        id.IsOk.Should().BeTrue();
        id.Value!.Name.Should().Be("NVIDIA GeForce RTX 5080");
        id.Value.Architecture.Should().Be(GpuArchitecture.Blackwell);
    }

    [Fact]
    public void Reads_ada_architecture_for_rtx_40()
    {
        var info = new NvApiInfo(new[] { Gpu("NVIDIA GeForce RTX 4070", "NVIDIA") });
        info.GetGpuIdentity(0).Value!.Architecture.Should().Be(GpuArchitecture.AdaOrOlder);
    }

    [Fact]
    public void Is_unavailable_and_fails_clean_when_no_nvidia_gpu()
    {
        var info = new NvApiInfo(new[] { Gpu("AMD Radeon RX 7900 XTX", "AMD") });

        info.IsAvailable.Should().BeFalse();
        var id = info.GetGpuIdentity(0);
        id.IsOk.Should().BeFalse();
        id.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void Out_of_range_index_fails_clean()
    {
        var info = new NvApiInfo(new[] { Gpu("NVIDIA GeForce RTX 4060", "NVIDIA") });
        info.GetGpuIdentity(5).Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        info.GetGpuIdentity(-1).Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public void Null_gpu_list_is_handled_fail_clean()
    {
        var info = new NvApiInfo(null!);
        info.IsAvailable.Should().BeFalse();
        info.GetGpuIdentity(0).IsOk.Should().BeFalse();
    }
}
