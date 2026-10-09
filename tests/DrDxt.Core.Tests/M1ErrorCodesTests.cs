using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

public class M1ErrorCodesTests
{
    // DET codes
    [Fact] public void WmiFail_is_stable()          => ErrorCodes.WmiFail.Should().Be("DET.WMI_FAIL");
    [Fact] public void DxgiFail_is_stable()         => ErrorCodes.DxgiFail.Should().Be("DET.DXGI_FAIL");
    [Fact] public void NvapiUnavailable_is_stable() => ErrorCodes.NvapiUnavailable.Should().Be("DET.NVAPI_UNAVAIL");
    [Fact] public void AdlxUnavailable_is_stable()  => ErrorCodes.AdlxUnavailable.Should().Be("DET.ADLX_UNAVAIL");
    [Fact] public void NoGpu_is_stable()            => ErrorCodes.NoGpu.Should().Be("DET.NO_GPU");
    [Fact] public void PowerFail_is_stable()        => ErrorCodes.PowerFail.Should().Be("DET.POWER_FAIL");
    [Fact] public void RegistryFail_is_stable()     => ErrorCodes.RegistryFail.Should().Be("DET.REG_FAIL");
    [Fact] public void FirmwareFail_is_stable()     => ErrorCodes.FirmwareFail.Should().Be("DET.FIRMWARE_FAIL");

    // MEAS codes
    [Fact] public void PmonUnavailable_is_stable()  => ErrorCodes.PmonUnavailable.Should().Be("MEAS.PMON_UNAVAIL");
    [Fact] public void CaptureFail_is_stable()      => ErrorCodes.CaptureFail.Should().Be("MEAS.CAPTURE_FAIL");
    [Fact] public void NoFrames_is_stable()         => ErrorCodes.NoFrames.Should().Be("MEAS.NO_FRAMES");
    [Fact] public void PidInvalid_is_stable()       => ErrorCodes.PidInvalid.Should().Be("MEAS.PID_INVALID");

    // MEAS codes — spine additions (M1.5)
    [Fact] public void NoPresentingFrames_is_stable() => ErrorCodes.NoPresentingFrames.Should().Be("MEAS.NO_PRESENTING_FRAMES");
    [Fact] public void ServiceNotInstalled_is_stable() => ErrorCodes.ServiceNotInstalled.Should().Be("MEAS.SERVICE_NOT_INSTALLED");

    [Fact]
    public void Spine_codes_are_distinct_from_pmon_unavailable()
    {
        ErrorCodes.NoPresentingFrames.Should().NotBe(ErrorCodes.PmonUnavailable);
        ErrorCodes.ServiceNotInstalled.Should().NotBe(ErrorCodes.PmonUnavailable);
        ErrorCodes.NoPresentingFrames.Should().NotBe(ErrorCodes.ServiceNotInstalled);
        ErrorCodes.NoPresentingFrames.Should().NotBe(ErrorCodes.NoFrames);
    }

    // SCAN codes
    [Fact] public void NoBaseline_is_stable()       => ErrorCodes.NoBaseline.Should().Be("SCAN.NO_BASELINE");

    // M0 constants must still be present
    [Fact] public void M0_DetectionFailed_unchanged()       => ErrorCodes.DetectionFailed.Should().Be("DETECTION_FAILED");
    [Fact] public void M0_BenchmarkFailed_unchanged()       => ErrorCodes.BenchmarkFailed.Should().Be("BENCHMARK_FAILED");
    [Fact] public void M0_RestorePointBlocked_unchanged()   => ErrorCodes.RestorePointBlocked.Should().Be("RESTORE_POINT_BLOCKED");
    [Fact] public void M0_ElevationDenied_unchanged()       => ErrorCodes.ElevationDenied.Should().Be("ELEVATION_DENIED");
    [Fact] public void HandshakeProtocolError_is_stable()   => ErrorCodes.HandshakeProtocolError.Should().Be("HANDSHAKE_PROTOCOL_ERROR");
    [Fact] public void HandshakeProtocolError_is_distinct_from_ElevationDenied() =>
        ErrorCodes.HandshakeProtocolError.Should().NotBe(ErrorCodes.ElevationDenied);
    [Fact] public void HelperUnreachable_is_stable()        => ErrorCodes.HelperUnreachable.Should().Be("HELPER_UNREACHABLE");
    [Fact] public void M0_SignatureInvalid_unchanged()      => ErrorCodes.SignatureInvalid.Should().Be("SIGNATURE_INVALID");
    [Fact] public void M0_ManifestTampered_unchanged()      => ErrorCodes.ManifestTampered.Should().Be("MANIFEST_TAMPERED");
    [Fact] public void M0_GuideLinkTampered_unchanged()     => ErrorCodes.GuideLinkTampered.Should().Be("GUIDE_LINK_TAMPERED");
    [Fact] public void M0_UpdateExpired_unchanged()         => ErrorCodes.UpdateExpired.Should().Be("UPDATE_EXPIRED");
    [Fact] public void M0_OpNotAllowed_unchanged()          => ErrorCodes.OpNotAllowed.Should().Be("OP_NOT_ALLOWED");
    [Fact] public void M0_IoFailure_unchanged()             => ErrorCodes.IoFailure.Should().Be("IO_FAILURE");
}
