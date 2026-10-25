using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxSlotsTests : garde à valeurs figées des index de slots de la vtable ADLX.
//
// Ces index sont un contrat ABI avec amdadlx64.dll : une mauvaise valeur appelle la
// mauvaise fonction native (plantage, écriture non voulue). Ils ne peuvent pas se tester en
// CI contre du vrai matériel, donc ce test fige chacun à la valeur vérifiée contre les
// en-têtes canoniques du SDK ADLX
// (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include/*.h). Si quelqu'un édite une
// constante, ceci échoue et renvoie à l'en-tête qui la définit.
// Table complète des citations : src/DrDxt.Detection/Gpu/Adlx/ADLX-SLOTS.md
// -----------------------------------------------------------------------
public sealed class AdlxSlotsTests
{
    [Fact] // base IADLXInterface (toutes les interfaces sauf IADLXSystem)
    public void BaseInterface_Slots_AreCanonical()
    {
        AdlxVtableSlots.Release.Should().Be(1);
        AdlxVtableSlots.QueryInterface.Should().Be(2);
    }

    [Fact] // ISystem.h — IADLXSystem (sans Acquire/Release)
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
        AdlxVtableSlots.Tuning_ResetToFactory.Should().Be(5);
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

    [Fact] // IGPUManualFanTuning.h : les écritures du ventilateur (vitesse fixe, Zero RPM) et les défauts de la v1
    public void Fan_Write_Slots_AreCanonical()
    {
        AdlxVtableSlots.Fan_GetEmptyTuningStates.Should().Be(5);
        AdlxVtableSlots.Fan_IsValidTuningStates.Should().Be(6);
        AdlxVtableSlots.Fan_SetTuningStates.Should().Be(7);
        AdlxVtableSlots.Fan_IsSupportedZeroRpm.Should().Be(8);
        AdlxVtableSlots.Fan_GetZeroRpmState.Should().Be(9);
        AdlxVtableSlots.Fan_SetZeroRpmState.Should().Be(10);
        AdlxVtableSlots.Fan1_GetDefaultTuningStates.Should().Be(23);
        AdlxVtableSlots.Fan1_GetDefaultZeroRpmState.Should().Be(27);
        AdlxVtableSlots.FanState_SetFanSpeed.Should().Be(4);
        AdlxVtableSlots.FanState_SetTemperature.Should().Be(6);
    }

    [Fact] // IGPUManualFanTuning.h — réglage de ventilation + liste d'états + état
    public void Fan_Slots_AreCanonical()
    {
        AdlxVtableSlots.Fan_GetTuningRanges.Should().Be(3);
        AdlxVtableSlots.Fan_GetTuningStates.Should().Be(4);
        AdlxVtableSlots.FanStateList_Size.Should().Be(3);
        AdlxVtableSlots.FanStateList_At.Should().Be(11);  // At_ManualFanTuningStateList
        AdlxVtableSlots.FanState_GetFanSpeed.Should().Be(3);
        AdlxVtableSlots.FanState_GetTemperature.Should().Be(5);
    }

    [Fact] // Lecture : ISystem.h, IGPUTuning1.h, ISmartAccessMemory.h, I3DSettings.h, IPerformanceMonitoring.h
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

    [Fact] // ADLX 32b5a740 : IADLXGPUMetricsVtbl (IPerformanceMonitoring.h), IADLX3DSettingsServices(1)Vtbl (I3DSettings(1).h)
    public void Metriques_et_reglages_Radeon_Slots_AreCanonical()
    {
        AdlxVtableSlots.Metrics_GpuClockSpeed.Should().Be(5);
        AdlxVtableSlots.Metrics_GpuHotspotTemperature.Should().Be(8);
        AdlxVtableSlots.Metrics_GpuPower.Should().Be(9);
        AdlxVtableSlots.Metrics_GpuTotalBoardPower.Should().Be(10);
        AdlxVtableSlots.Metrics_GpuFanSpeed.Should().Be(11);
        AdlxVtableSlots.Settings3D_GetBoost.Should().Be(5);
        AdlxVtableSlots.Settings3D_GetImageSharpening.Should().Be(6);
        AdlxVtableSlots.Settings3D_GetRadeonSuperResolution.Should().Be(14);
        AdlxVtableSlots.Settings3D1_GetAmdFluidMotionFrames.Should().Be(17);
    }

    [Fact] // ADLX 32b5a740 : ISystem.h, IDisplays.h, IDisplaySettings.h
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
