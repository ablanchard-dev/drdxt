namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxVtableSlots — canonical ADLX COM vtable slot indices (0-based).
//
// ADLX exposes a COM-like C ABI: each object's first word points to a vtable
// (an array of function pointers). To call a method we index that array. These
// indices are not arbitrary — they are fixed by the ADLX SDK interface
// declarations. Indexing the wrong slot calls the WRONG native function.
//
// Every constant below was verified slot-by-slot on 2026-06-29 against the
// canonical ADLX SDK headers:
//   https://github.com/GPUOpen-LibrariesAndSDKs/ADLX  (SDK/Include/*.h)
// See ADLX-SLOTS.md (same folder) for the full per-slot citation table.
//
// AdlxNative is the only consumer. AdlxSlotsTests freezes each value so an
// accidental edit fails the build loudly instead of silently mis-calling the
// driver — these cannot be unit-tested against real hardware in CI.
// -----------------------------------------------------------------------
public static class AdlxVtableSlots
{
    // IADLXInterface — base of every interface EXCEPT IADLXSystem.
    //   slot 0 = Acquire (never called here)
    public const int Release        = 1;
    public const int QueryInterface = 2;

    // IADLXSystem (ISystem.h) — SPECIAL: no Acquire/Release; methods start at 0.
    public const int System_GetGPUs              = 1;
    public const int System_GetGPUTuningServices = 8;

    // IADLXGPUList (ISystem.h) — IADLXInterface + IADLXList + GPU-list methods.
    public const int GpuList_Size = 3;
    public const int GpuList_At   = 11; // At_GPUList

    // IADLXGPUTuningServices (IGPUTuning.h)
    public const int Tuning_IsSupportedManualGfx   = 8;
    public const int Tuning_IsSupportedManualFan   = 10;
    public const int Tuning_IsSupportedManualPower = 11;
    public const int Tuning_GetManualGfx           = 14;
    public const int Tuning_GetManualFan           = 16;
    public const int Tuning_GetManualPower         = 17;

    // IADLXManualGraphicsTuning2 (IGPUManualGFXTuning.h) — RDNA freq+voltage API.
    // NOTE: IADLXManualGraphicsTuning1 is a DIFFERENT, states-based interface;
    // we never call its own slots — only QueryInterface (slot 2) to upgrade to v2.
    public const int Gfx2_GetMinFreqRange = 3;
    public const int Gfx2_GetMinFreq      = 4;
    public const int Gfx2_GetMaxFreqRange = 6;
    public const int Gfx2_GetMaxFreq      = 7;
    public const int Gfx2_SetMaxFreq      = 8;  // Write
    public const int Gfx2_GetVoltRange    = 9;
    public const int Gfx2_GetVolt         = 10;
    public const int Gfx2_SetVolt         = 11; // Write

    // IADLXManualPowerTuning (IGPUManualPowerTuning.h)
    public const int Power_GetLimitRange = 3;
    public const int Power_GetLimit      = 4;
    public const int Power_SetLimit      = 5;  // Write

    // IADLXManualFanTuning (IGPUManualFanTuning.h) — read-only path here.
    public const int Fan_GetTuningRanges = 3;
    public const int Fan_GetTuningStates = 4;

    // IADLXManualFanTuningStateList (IGPUManualFanTuning.h)
    public const int FanStateList_Size = 3;
    public const int FanStateList_At   = 11; // At_ManualFanTuningStateList

    // IADLXManualFanTuningState (IGPUManualFanTuning.h)
    public const int FanState_GetFanSpeed    = 3;
    public const int FanState_GetTemperature = 5;

    // ── Lecture des réglages (A1, 06/10/2026). Relevées dans les blocs C `...Vtbl` des en-têtes du SDK
    //    (branche main, téléchargés le 06/10/2026)  (https://github.com/GPUOpen-LibrariesAndSDKs/ADLX).

    // IADLXSystem (ISystem.h)
    public const int System_Get3DSettingsServices            = 7;
    public const int System_GetPerformanceMonitoringServices = 9;

    // IADLXGPU (ISystem.h) : Type rend ADLX_GPU_TYPE (0 inconnu, 1 intégrée, 2 dédiée).
    public const int Gpu_Type = 5;

    // IADLXGPUTuningServices1 (IGPUTuning1.h), obtenue par QueryInterface depuis IADLXGPUTuningServices.
    public const int Tuning1_GetSmartAccessMemory = 18;

    // IADLXSmartAccessMemory (ISmartAccessMemory.h), IADLX3DAntiLag, IADLX3DChill,
    // IADLX3DFrameRateTargetControl (I3DSettings.h) : même tête IsSupported / IsEnabled.
    public const int Feature_IsSupported = 3;
    public const int Feature_IsEnabled   = 4;

    // IADLX3DAntiLag (I3DSettings.h, ADLX d9f04a9b du 17/04/2026) : 3 IsSupported, 4 IsEnabled, 5 SetEnabled.
    public const int AntiLag_SetEnabled = 5;

    // IADLX3DSettingsServices (I3DSettings.h)
    public const int Settings3D_GetAntiLag                = 3;
    public const int Settings3D_GetChill                  = 4;
    public const int Settings3D_GetFrameRateTargetControl = 9;
    // A2c (08/10/2026), relues dans ADLX d9f04a9b : Enhanced Sync 7, Vsync (WaitForVerticalRefresh) 8.
    public const int Settings3D_GetEnhancedSync = 7;
    public const int Settings3D_GetWaitForVerticalRefresh = 8;

    // IADLX3DWaitForVerticalRefresh (I3DSettings.h) : 3 IsSupported, 4 IsEnabled, 5 GetMode, 6 SetMode.
    public const int Wfvr_GetMode = 5;
    public const int Wfvr_SetMode = 6;

    // IADLX3DChill (I3DSettings.h)
    public const int Chill_GetMinFps = 6;
    public const int Chill_GetMaxFps = 7;

    // IADLX3DFrameRateTargetControl (I3DSettings.h) ; écritures relues dans ADLX d9f04a9b (A2b, 07/10/2026).
    public const int Frtc_GetFpsRange = 5;
    public const int Frtc_GetFps = 6;
    public const int Frtc_SetEnabled = 7;
    public const int Frtc_SetFps = 8;

    // A-FS1 (08/10/2026) : FreeSync, relevé dans les blocs C `...Vtbl` d'ADLX 32b5a740 (ISystem.h, IDisplays.h,
    // IDisplaySettings.h). Lecture seule : SetEnabled (5) n'est jamais appelé.
    public const int System_GetDisplaysServices      = 3;
    public const int DisplayServices_GetDisplays     = 4;
    public const int DisplayServices_GetFreeSync     = 9;
    public const int DisplayList_Size                = 3;
    public const int DisplayList_At                  = 11; // At_DisplayList
    public const int Display_Name                    = 6;
    // IADLXDisplayFreeSync : 3 IsSupported, 4 IsEnabled = Feature_IsSupported / Feature_IsEnabled.

    // IADLXPerformanceMonitoringServices / IADLXGPUMetrics (IPerformanceMonitoring.h)
    public const int Perf_GetCurrentGpuMetrics = 18;
    public const int Metrics_GpuTemperature    = 7;
}
