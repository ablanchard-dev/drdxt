# ADLX vtable slot reference

`AdlxNative` calls into `amdadlx64.dll` through ADLX's COM-like C ABI: each
object's first word points to a vtable (an array of function pointers), and a
method is invoked by indexing that array. The index of every method is fixed by
the ADLX SDK interface declaration — indexing the wrong slot calls the wrong
native function, which on the write path means an unintended driver write or a
process crash.

Every slot below was verified **2026-06-29**, slot by slot, against the canonical
ADLX SDK headers at
<https://github.com/GPUOpen-LibrariesAndSDKs/ADLX> (`SDK/Include/*.h`). The
constants live in `AdlxVtableSlots.cs`; `AdlxSlotsTests` freezes each value so a
later edit fails the build.

## Verified slots

| Interface (header) | Method | Slot |
|---|---|---|
| IADLXInterface | Release | 1 |
| IADLXInterface | QueryInterface | 2 |
| IADLXSystem (`ISystem.h`) — no Acquire/Release | GetGPUs | 1 |
| IADLXSystem (`ISystem.h`) | GetGPUTuningServices | 8 |
| IADLXGPUList (`ISystem.h`) | Size | 3 |
| IADLXGPUList (`ISystem.h`) | At_GPUList | 11 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | ResetToFactory | 5 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | IsSupportedManualGFXTuning | 8 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | IsSupportedManualFanTuning | 10 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | IsSupportedManualPowerTuning | 11 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | GetManualGFXTuning | 14 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | GetManualFanTuning | 16 |
| IADLXGPUTuningServices (`IGPUTuning.h`) | GetManualPowerTuning | 17 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUMinFrequencyRange | 3 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUMinFrequency | 4 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUMaxFrequencyRange | 6 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUMaxFrequency | 7 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | **SetGPUMaxFrequency** | **8** |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUVoltageRange | 9 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | GetGPUVoltage | 10 |
| IADLXManualGraphicsTuning2 (`IGPUManualGFXTuning.h`) | **SetGPUVoltage** | **11** |
| IADLXManualPowerTuning (`IGPUManualPowerTuning.h`) | GetPowerLimitRange | 3 |
| IADLXManualPowerTuning (`IGPUManualPowerTuning.h`) | GetPowerLimit | 4 |
| IADLXManualPowerTuning (`IGPUManualPowerTuning.h`) | **SetPowerLimit** | **5** |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | GetFanTuningRanges | 3 |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | GetFanTuningStates | 4 |
| IADLXManualFanTuningStateList (`IGPUManualFanTuning.h`) | Size | 3 |
| IADLXManualFanTuningStateList (`IGPUManualFanTuning.h`) | At_ManualFanTuningStateList | 11 |
| IADLXManualFanTuningState (`IGPUManualFanTuning.h`) | GetFanSpeed | 3 |
| IADLXManualFanTuningState (`IGPUManualFanTuning.h`) | GetTemperature | 5 |

## Ventilateur en écriture (relevé le 25/10/2026)

Relevées dans `SDK/Include/IGPUManualFanTuning.h` de la branche `main`
(<https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/main/SDK/Include/IGPUManualFanTuning.h>). Aucune n'a tourné sur
une vraie carte AMD : le ventilateur du mode Expert reste fermé sur AMD tant qu'une architecture n'a pas son rapport.

| Interface (header) | Method | Slot |
|---|---|---|
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | GetEmptyFanTuningStates | 5 |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | IsValidFanTuningStates | 6 |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | **SetFanTuningStates** | **7** |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | IsSupportedZeroRPM | 8 |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | GetZeroRPMState | 9 |
| IADLXManualFanTuning (`IGPUManualFanTuning.h`) | **SetZeroRPMState** | **10** |
| IADLXManualFanTuning1 (`IGPUManualFanTuning.h`), par QueryInterface | GetDefaultFanTuningStates | 23 |
| IADLXManualFanTuning1 (`IGPUManualFanTuning.h`), par QueryInterface | GetDefaultZeroRPMState | 27 |
| IADLXManualFanTuningState (`IGPUManualFanTuning.h`) | **SetFanSpeed** (en %) | **4** |
| IADLXManualFanTuningState (`IGPUManualFanTuning.h`) | **SetTemperature** (en °C) | **6** |

## Lecture des réglages (A1, relevé le 06/10/2026)

Relevées dans les blocs C `typedef struct ...Vtbl` des en-têtes de la branche `main` du SDK (même dépôt), lecture
seule. Aucune n'a encore tourné sur une vraie carte AMD : la commande de preuve est dans la fiche de recherche
`research/north-star/adlx-lecture.md`.

| Interface (header) | Method | Slot |
|---|---|---|
| IADLXSystem (`ISystem.h`) | Get3DSettingsServices | 7 |
| IADLXSystem (`ISystem.h`) | GetPerformanceMonitoringServices | 9 |
| IADLXGPU (`ISystem.h`) | Type (ADLX_GPU_TYPE : 2 = dédiée) | 5 |
| IADLXGPUTuningServices1 (`IGPUTuning1.h`), par QueryInterface | GetSmartAccessMemory | 18 |
| IADLXSmartAccessMemory (`ISmartAccessMemory.h`) | IsSupported / IsEnabled | 3 / 4 |
| IADLX3DSettingsServices (`I3DSettings.h`) | GetAntiLag / GetChill / GetEnhancedSync / GetWaitForVerticalRefresh / GetFrameRateTargetControl | 3 / 4 / 7 / 8 / 9 |
| IADLX3DWaitForVerticalRefresh (`I3DSettings.h`) | IsSupported / IsEnabled / GetMode / SetMode (A2c du 08/10/2026, relu dans ADLX `d9f04a9b`) | 3 / 4 / 5 / 6 |
| IADLX3DEnhancedSync (`I3DSettings.h`) | IsSupported / IsEnabled | 3 / 4 |
| IADLX3DAntiLag (`I3DSettings.h`) | IsSupported / IsEnabled / SetEnabled (écriture, A2 du 07/10/2026, relu dans ADLX `d9f04a9b`) | 3 / 4 / 5 |
| IADLX3DChill (`I3DSettings.h`) | IsSupported / IsEnabled / GetMinFPS / GetMaxFPS | 3 / 4 / 6 / 7 |
| IADLX3DFrameRateTargetControl (`I3DSettings.h`) | IsSupported / IsEnabled / GetFPSRange / GetFPS / SetEnabled / SetFPS (écritures A2b du 07/10/2026, relues dans ADLX `d9f04a9b`) | 3 / 4 / 5 / 6 / 7 / 8 |
| IADLXPerformanceMonitoringServices (`IPerformanceMonitoring.h`) | GetCurrentGPUMetrics | 18 |
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUTemperature | 7 |

## Télémétrie en partie et réglages Radeon (relevé le 25/10/2026)

Relevées dans les blocs C `typedef struct ...Vtbl` des en-têtes du SDK, commit `32b5a740d422` de la branche `main`
(<https://github.com/GPUOpen-LibrariesAndSDKs/ADLX>, `SDK/Include`), membre par membre dans l'ordre de déclaration. Lecture
seule : aucune entrée `Set*` n'est appelée. Aucune n'a encore tourné sur une vraie carte AMD.

| Interface (header) | Method | Slot |
|---|---|---|
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUClockSpeed (`adlx_int`, MHz) | 5 |
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUHotspotTemperature (`adlx_double`, °C) | 8 |
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUPower (`adlx_double`, W, la puce) | 9 |
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUTotalBoardPower (`adlx_double`, W, la carte) | 10 |
| IADLXGPUMetrics (`IPerformanceMonitoring.h`) | GPUFanSpeed (`adlx_int`, tr/min) | 11 |
| IADLX3DSettingsServices (`I3DSettings.h`) | GetBoost (par carte) / GetImageSharpening (par carte) | 5 / 6 |
| IADLX3DSettingsServices (`I3DSettings.h`) | GetRadeonSuperResolution (système) | 14 |
| IADLX3DSettingsServices1 (`I3DSettings1.h`), par QueryInterface `L"IADLX3DSettingsServices1"` | GetAMDFluidMotionFrames (système) | 17 |
| IADLX3DBoost / IADLX3DImageSharpening / IADLX3DRadeonSuperResolution / IADLX3DAMDFluidMotionFrames | IsSupported / IsEnabled | 3 / 4 |

HYPR-RX n'a pas d'interface dans le SDK ADLX (aucun en-tête de `SDK/Include` ne le nomme au commit `32b5a740d422`) : il
n'est pas lu.

## Notes

- **IADLXManualGraphicsTuning1 vs 2.** Tuning1 is a *states-based* interface
  (slot 3 = `GetGPUTuningRanges`, slot 7 = `SetGPUTuningStates`) and is unrelated
  to Tuning2's frequency/voltage layout. `AdlxNative` obtains Tuning1 only to call
  `QueryInterface` (slot 2) and upgrade to Tuning2; it never calls Tuning1's own
  slots. On cards that do not expose Tuning2 (Vega/older) the read gate returns
  "not readable" and the write path is skipped — no fake value, no write.
- **IADLXSystem is special.** It does not derive from IADLXInterface, so it has
  no `Acquire`/`Release` and its own methods begin at slot 0.
- All interfaces deriving from IADLXInterface share slots 0/1/2 =
  `Acquire`/`Release`/`QueryInterface`; list interfaces then carry the IADLXList
  methods (Size=3 … Add_Back=10) before their typed `At_*` at slot 11.
