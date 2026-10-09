using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxSlotsTests — frozen-value guard for the ADLX vtable slot indices.
//
// These indices are an ABI contract with amdadlx64.dll: a wrong value calls
// the WRONG native function (crash / unintended write). They cannot be unit-
// tested against real hardware in CI, so this test FREEZES each one to the
// value verified on 2026-06-29 against the canonical ADLX SDK headers
// (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include/*.h). If anyone edits a
// constant, this fails and points back to the header that defines it.
// Full citation table: src/DrDxt.Detection/Gpu/Adlx/ADLX-SLOTS.md
// -----------------------------------------------------------------------
public sealed class AdlxSlotsTests
{
    [Fact] // IADLXInterface base (all interfaces except IADLXSystem)
    public void BaseInterface_Slots_AreCanonical()
    {
        AdlxVtableSlots.Release.Should().Be(1);
        AdlxVtableSlots.QueryInterface.Should().Be(2);
    }

    [Fact] // ISystem.h — IADLXSystem (no Acquire/Release)
    public void System_Slots_AreCanonical()
    {
        AdlxVtableSlots.System_GetGPUs.Should().Be(1);
        AdlxVtableSlots.System_GetGPUTuningServices.Should().Be(8);
    }

    [Fact] // ISystem.h — IADLXGPUList
    public void GpuList_Slots_AreCanonical()
    {
        AdlxVtableSlots.GpuList_Size.Should().Be(3);
        AdlxVtableSlots.GpuList_At.Should().Be(11); // At_GPUList
    }

    [Fact] // IGPUTuning.h — IADLXGPUTuningServices
    public void TuningServices_Slots_AreCanonical()
    {
        AdlxVtableSlots.Tuning_IsSupportedManualGfx.Should().Be(8);
        AdlxVtableSlots.Tuning_IsSupportedManualFan.Should().Be(10);
        AdlxVtableSlots.Tuning_IsSupportedManualPower.Should().Be(11);
        AdlxVtableSlots.Tuning_GetManualGfx.Should().Be(14);
        AdlxVtableSlots.Tuning_GetManualFan.Should().Be(16);
        AdlxVtableSlots.Tuning_GetManualPower.Should().Be(17);
    }

    [Fact] // IGPUManualGFXTuning.h — IADLXManualGraphicsTuning2
    public void Gfx2_Slots_AreCanonical()
    {
        AdlxVtableSlots.Gfx2_GetMinFreqRange.Should().Be(3);
        AdlxVtableSlots.Gfx2_GetMinFreq.Should().Be(4);
        AdlxVtableSlots.Gfx2_GetMaxFreqRange.Should().Be(6);
        AdlxVtableSlots.Gfx2_GetMaxFreq.Should().Be(7);
        AdlxVtableSlots.Gfx2_SetMaxFreq.Should().Be(8);   // Write
        AdlxVtableSlots.Gfx2_GetVoltRange.Should().Be(9);
        AdlxVtableSlots.Gfx2_GetVolt.Should().Be(10);
        AdlxVtableSlots.Gfx2_SetVolt.Should().Be(11);     // Write
    }

    [Fact] // IGPUManualPowerTuning.h — IADLXManualPowerTuning
    public void Power_Slots_AreCanonical()
    {
        AdlxVtableSlots.Power_GetLimitRange.Should().Be(3);
        AdlxVtableSlots.Power_GetLimit.Should().Be(4);
        AdlxVtableSlots.Power_SetLimit.Should().Be(5);    // Write
    }

    [Fact] // IGPUManualFanTuning.h — fan tuning + state list + state
    public void Fan_Slots_AreCanonical()
    {
        AdlxVtableSlots.Fan_GetTuningRanges.Should().Be(3);
        AdlxVtableSlots.Fan_GetTuningStates.Should().Be(4);
        AdlxVtableSlots.FanStateList_Size.Should().Be(3);
        AdlxVtableSlots.FanStateList_At.Should().Be(11);  // At_ManualFanTuningStateList
        AdlxVtableSlots.FanState_GetFanSpeed.Should().Be(3);
        AdlxVtableSlots.FanState_GetTemperature.Should().Be(5);
    }

    [Fact] // A1 (06/10/2026) — lecture : ISystem.h, IGPUTuning1.h, ISmartAccessMemory.h, I3DSettings.h, IPerformanceMonitoring.h
    public void Lecture_Slots_AreCanonical()
    {
        AdlxVtableSlots.System_Get3DSettingsServices.Should().Be(7);
        AdlxVtableSlots.System_GetPerformanceMonitoringServices.Should().Be(9);
        AdlxVtableSlots.Gpu_Type.Should().Be(5);
        AdlxVtableSlots.Tuning1_GetSmartAccessMemory.Should().Be(18);
        AdlxVtableSlots.Feature_IsSupported.Should().Be(3);
        AdlxVtableSlots.Feature_IsEnabled.Should().Be(4);
        AdlxVtableSlots.AntiLag_SetEnabled.Should().Be(5);
        AdlxVtableSlots.Settings3D_GetAntiLag.Should().Be(3);
        AdlxVtableSlots.Settings3D_GetChill.Should().Be(4);
        AdlxVtableSlots.Settings3D_GetFrameRateTargetControl.Should().Be(9);
        AdlxVtableSlots.Chill_GetMinFps.Should().Be(6);
        AdlxVtableSlots.Chill_GetMaxFps.Should().Be(7);
        AdlxVtableSlots.Frtc_GetFps.Should().Be(6);
        AdlxVtableSlots.Frtc_GetFpsRange.Should().Be(5);
        AdlxVtableSlots.Frtc_SetEnabled.Should().Be(7);
        AdlxVtableSlots.Frtc_SetFps.Should().Be(8);
        AdlxVtableSlots.Settings3D_GetEnhancedSync.Should().Be(7);
        AdlxVtableSlots.Settings3D_GetWaitForVerticalRefresh.Should().Be(8);
        AdlxVtableSlots.Wfvr_GetMode.Should().Be(5);
        AdlxVtableSlots.Wfvr_SetMode.Should().Be(6);
        AdlxVtableSlots.Perf_GetCurrentGpuMetrics.Should().Be(18);
        AdlxVtableSlots.Metrics_GpuTemperature.Should().Be(7);
    }

    [Fact] // A-FS1 (08/10/2026) — ADLX 32b5a740 : ISystem.h, IDisplays.h, IDisplaySettings.h
    public void FreeSync_Slots_AreCanonical()
    {
        AdlxVtableSlots.System_GetDisplaysServices.Should().Be(3);
        AdlxVtableSlots.DisplayServices_GetDisplays.Should().Be(4);
        AdlxVtableSlots.DisplayServices_GetFreeSync.Should().Be(9);
        AdlxVtableSlots.DisplayList_Size.Should().Be(3);
        AdlxVtableSlots.DisplayList_At.Should().Be(11);
        AdlxVtableSlots.Display_Name.Should().Be(6);
    }
}
