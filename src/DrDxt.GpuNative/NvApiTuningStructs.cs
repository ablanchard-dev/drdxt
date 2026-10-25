using System.Runtime.InteropServices;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// Dispositions des structs NVAPI rétro-ingéniées pour la surface de réglage risquée, reprises
// octet pour octet de NvAPIWrapper (PerformanceStates20*.cs, PrivatePowerPolicies*V1.cs ; chaque
// struct en Pack = 8). Des champs int/uint bruts (pas des enums) gardent la taille marshalée exacte
// et ce fichier autonome.
//
// Invariant porteur : le Marshal.SizeOf de chaque struct doit égaler la taille de la struct C de
// NVIDIA (épinglé par NvApiStructLayoutTests). Le mot de version est calculé depuis Marshal.SizeOf
// à l'exécution (NvApiEntry.MakeVersion) : toute dérive remonte comme l'IncompatibleStructVersion
// (-9) sûr du pilote, un rejet propre, jamais une écriture corrompue. Chaque ByValArray est
// préalloué à son SizeConst par les fabriques de NvApiNative avant le marshaling (sinon
// PtrToStructure lève).
// -----------------------------------------------------------------------

internal static class NvApiTuningConstants
{
    public const int MaxPstates = 16;        // NVAPI_MAX_GPU_PERF_PSTATES
    public const int MaxClocks = 8;          // NVAPI_MAX_GPU_PSTATE20_CLOCKS
    public const int MaxBaseVoltages = 4;    // NVAPI_MAX_GPU_PSTATE20_BASE_VOLTAGES
    public const int MaxPowerEntries = 4;    // NVAPI_MAX_POWER_POLICIES_*_ENTRIES
}

/// <summary>NV_GPU_PERF_PSTATES20_PARAM_DELTA : une valeur de delta + sa plage valide, dans
/// l'unité du champ (kHz pour les horloges, uV pour les tensions). 12 octets.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPerfPstates20ParameterDelta
{
    public int Value;
    public int RangeMin;
    public int RangeMax;
}

/// <summary>NV_GPU_PSTATE20_CLOCK_ENTRY_V1. 44 octets. On ne lit / n'écrit que le delta de
/// fréquence ; l'union finale dépendant de l'horloge (fréquence unique / plage, 20 octets max) est
/// gardée en cinq uint bruts pour la seule taille exacte.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPstate20ClockEntryV1
{
    public uint DomainId;     // PublicClockDomain : 0 = GRAPHICS
    public uint ClockType;    // 0 = single, 1 = range
    public uint Flags;        // bit0 = editable
    public NvGpuPerfPstates20ParameterDelta FreqDeltaKhz;
    public uint Dep0;         // union dépendant de l'horloge (inutilisée ici)
    public uint Dep1;
    public uint Dep2;
    public uint Dep3;
    public uint Dep4;
}

/// <summary>NV_GPU_PSTATE20_BASE_VOLTAGE_ENTRY_V1. 24 octets. Présente seulement pour dimensionner
/// correctement le tableau par pstate ; on ne modifie jamais les tensions (horloge et puissance seulement).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvGpuPstate20BaseVoltageEntryV1
{
    public uint DomainId;
    public uint Flags;
    public uint VoltageUv;
    public NvGpuPerfPstates20ParameterDelta VoltageDeltaUv;
}

/// <summary>Entrée par pstate de NV_GPU_PERF_PSTATES20_INFO_V1. 456 octets.</summary>
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

/// <summary>NV_GPU_PERF_PSTATES20_INFO_V1. 7316 octets. version = MAKE_NVAPI_VERSION(this,1).</summary>
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

// --- politiques de puissance privées (recette §B) ---

/// <summary>Entrée NV_GPU_POWER_STATUS_V1. 16 octets. PowerTargetPcm est en pour cent mille.</summary>
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

/// <summary>Entrée NV_GPU_POWER_INFO_V1. 44 octets. Min/Default/Max sont en pour cent mille.</summary>
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
