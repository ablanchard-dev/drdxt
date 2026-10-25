namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxVtableSlots : les indices de référence des emplacements de vtable COM d'ADLX (à partir de 0).
//
// ADLX expose une ABI C proche de COM : le premier mot de chaque objet pointe vers une vtable (un
// tableau de pointeurs de fonction). Pour appeler une méthode, on indexe ce tableau. Ces indices
// ne sont pas arbitraires : ils sont fixés par les déclarations d'interfaces du SDK ADLX. Indexer
// le mauvais emplacement appelle la mauvaise fonction native.
//
// Chaque constante ci-dessous a été vérifiée emplacement par emplacement le 2026-06-29 contre les
// en-têtes de référence du SDK ADLX :
//   https://github.com/GPUOpen-LibrariesAndSDKs/ADLX  (SDK/Include/*.h)
// Voir ADLX-SLOTS.md (même dossier) pour le tableau complet des citations par emplacement.
//
// AdlxNative est le seul consommateur. AdlxSlotsTests fige chaque valeur pour qu'une modification
// accidentelle fasse échouer le build bruyamment au lieu d'appeler le pilote de travers en silence :
// on ne peut pas les tester sur du vrai matériel en CI.
// -----------------------------------------------------------------------
public static class AdlxVtableSlots
{
    // IADLXInterface : base de toutes les interfaces sauf IADLXSystem.
    //   emplacement 0 = Acquire (jamais appelé ici)
    public const int Release        = 1;
    public const int QueryInterface = 2;

    // IADLXSystem (ISystem.h), spécial : pas d'Acquire/Release ; les méthodes commencent à 0.
    public const int System_GetGPUs              = 1;
    public const int System_GetGPUTuningServices = 8;

    // IADLXGPUList (ISystem.h) : IADLXInterface + IADLXList + méthodes de liste de GPU.
    public const int GpuList_Size = 3;
    public const int GpuList_At   = 11; // At_GPUList

    // IADLXGPUTuningServices (IGPUTuning.h)
    public const int Tuning_IsAtFactory            = 4; // Read : le réglage de la carte est-il à l'usine
    public const int Tuning_ResetToFactory         = 5; // Write : remet tout le réglage de la carte à l'usine
    public const int Tuning_IsSupportedManualGfx   = 8;
    public const int Tuning_IsSupportedManualFan   = 10;
    public const int Tuning_IsSupportedManualPower = 11;
    public const int Tuning_GetManualGfx           = 14;
    public const int Tuning_GetManualFan           = 16;
    public const int Tuning_GetManualPower         = 17;

    // IADLXManualGraphicsTuning2 (IGPUManualGFXTuning.h) : l'API fréquence + tension de RDNA.
    // Note : IADLXManualGraphicsTuning1 est une autre interface, par états ; on n'appelle jamais ses
    // propres emplacements, seulement QueryInterface (emplacement 2) pour passer à la v2.
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

    // IADLXManualFanTuning (IGPUManualFanTuning.h) : chemin en lecture seule ici.
    public const int Fan_GetTuningRanges = 3;
    public const int Fan_GetTuningStates = 4;

    // IADLXManualFanTuningStateList (IGPUManualFanTuning.h)
    public const int FanStateList_Size = 3;
    public const int FanStateList_At   = 11; // At_ManualFanTuningStateList

    // IADLXManualFanTuningState (IGPUManualFanTuning.h)
    public const int FanState_GetFanSpeed    = 3;
    public const int FanState_GetTemperature = 5;

    // ── Lecture des réglages. Relevées dans les blocs C `...Vtbl` des en-têtes du SDK
    //    (source : branche main, version téléchargée le 06/10/2026, https://github.com/GPUOpen-LibrariesAndSDKs/ADLX).

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

    // IADLX3DAntiLag (I3DSettings.h, source ADLX d9f04a9b du 17/04/2026) : 3 IsSupported, 4 IsEnabled, 5 SetEnabled.
    public const int AntiLag_SetEnabled = 5;

    // IADLX3DSettingsServices (I3DSettings.h)
    public const int Settings3D_GetAntiLag                = 3;
    public const int Settings3D_GetChill                  = 4;
    public const int Settings3D_GetFrameRateTargetControl = 9;
    // A2c, relues dans ADLX d9f04a9b : Enhanced Sync 7, Vsync (WaitForVerticalRefresh) 8.
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

    // FreeSync, relevé dans les blocs C `...Vtbl` d'ADLX 32b5a740 (ISystem.h, IDisplays.h,
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
