using System.Runtime.InteropServices;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// Reverse-engineered NVAPI struct layouts for the RISKY tuning surface, mirrored
// byte-for-byte from NvAPIWrapper (PerformanceStates20*.cs, PrivatePowerPolicies*V1.cs;
// every struct Pack = 8). Raw int/uint fields (not enums) keep the marshalled size
// exact and this file self-contained.
//
// Load-bearing invariant: each struct's Marshal.SizeOf must equal NVIDIA's C struct
// size (pinned by NvApiStructLayoutTests). The version word is computed from
// Marshal.SizeOf at runtime (NvApiEntry.MakeVersion), so any drift surfaces as the
// driver's SAFE IncompatibleStructVersion (-7) — a clean rejection, never a corrupt
// write. Every ByValArray is pre-allocated to its SizeConst by the NvApiNative
// factories before marshalling (else PtrToStructure throws).
// -----------------------------------------------------------------------

internal static class NvApiTuningConstants
{
    public const int MaxPstates = 16;        // NVAPI_MAX_GPU_PERF_PSTATES
    public const int MaxClocks = 8;          // NVAPI_MAX_GPU_PSTATE20_CLOCKS
    public const int MaxBaseVoltages = 4;    // NVAPI_MAX_GPU_PSTATE20_BASE_VOLTAGES
    public const int MaxPowerEntries = 4;    // NVAPI_MAX_POWER_POLICIES_*_ENTRIES
}

/// <summary>NV_GPU_PERF_PSTATES20_PARAM_DELTA: a delta value + its valid range,
/// in the field's unit (kHz for clocks, uV for voltages). 12 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPerfPstates20ParameterDelta
{
    public int Value;
    public int RangeMin;
    public int RangeMax;
}

/// <summary>NV_GPU_PSTATE20_CLOCK_ENTRY_V1. 44 bytes. We only read/write the frequency
/// delta; the trailing clock-dependent union (single freq / range, max 20 bytes) is
/// kept as five raw uints purely to preserve the exact size.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPstate20ClockEntryV1
{
    public uint DomainId;     // PublicClockDomain: 0 = GRAPHICS
    public uint ClockType;    // 0 = single, 1 = range
    public uint Flags;        // bit0 = editable
    public NvGpuPerfPstates20ParameterDelta FreqDeltaKhz;
    public uint Dep0;         // clock-dependent union (unused here)
    public uint Dep1;
    public uint Dep2;
    public uint Dep3;
    public uint Dep4;
}

/// <summary>NV_GPU_PSTATE20_BASE_VOLTAGE_ENTRY_V1. 24 bytes. Present only to size the
/// per-pstate array correctly; we never edit voltages (Option A = clock/power only).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPstate20BaseVoltageEntryV1
{
    public uint DomainId;
    public uint Flags;
    public uint VoltageUv;
    public NvGpuPerfPstates20ParameterDelta VoltageDeltaUv;
}

/// <summary>NV_GPU_PERF_PSTATES20_INFO_V1 per-pstate entry. 456 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPerformanceState20
{
    public uint StateId;   // PerformanceStateId: 0 = P0
    public uint Flags;     // bit0 = editable

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApiTuningConstants.MaxClocks)]
    public NvGpuPstate20ClockEntryV1[] Clocks;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApiTuningConstants.MaxBaseVoltages)]
    public NvGpuPstate20BaseVoltageEntryV1[] BaseVoltages;
}

/// <summary>NV_GPU_PERF_PSTATES20_INFO_V1. 7316 bytes. version = MAKE_NVAPI_VERSION(this,1).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPerfPstates20InfoV1
{
    public uint Version;
    public uint Flags;            // bit0 = editable
    public uint NumPstates;
    public uint NumClocks;
    public uint NumBaseVoltages;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApiTuningConstants.MaxPstates)]
    public NvGpuPerformanceState20[] Pstates;
}

// --- private power policies (recipe §B) ---

/// <summary>NV_GPU_POWER_STATUS_V1 entry. 16 bytes. PowerTargetPcm is per-cent-mille.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPowerPolicyStatusEntry
{
    public uint PstateId;
    public uint Unknown1;
    public uint PowerTargetPcm;
    public uint Unknown2;
}

/// <summary>NV_GPU_POWER_STATUS_V1 (ClientPowerPoliciesGet/SetStatus). 72 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuClientPowerPoliciesStatusV1
{
    public uint Version;
    public uint Count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApiTuningConstants.MaxPowerEntries)]
    public NvGpuPowerPolicyStatusEntry[] Entries;
}

/// <summary>NV_GPU_POWER_INFO_V1 entry. 44 bytes. Min/Default/Max are per-cent-mille.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPowerPolicyInfoEntry
{
    public uint StateId;
    public uint Unknown1;
    public uint Unknown2;
    public uint MinPowerPcm;
    public uint Unknown3;
    public uint Unknown4;
    public uint DefaultPowerPcm;
    public uint Unknown5;
    public uint Unknown6;
    public uint MaxPowerPcm;
    public uint Unknown7;
}

/// <summary>NV_GPU_POWER_INFO_V1 (ClientPowerPoliciesGetInfo). 184 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuClientPowerPoliciesInfoV1
{
    public uint Version;
    public byte Valid;
    public byte Count;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvApiTuningConstants.MaxPowerEntries)]
    public NvGpuPowerPolicyInfoEntry[] Entries;
}
