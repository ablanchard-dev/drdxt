namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiStatus — the subset of NvAPI_Status codes we branch on.
//
// Golden safety rule #3 (recipe): 0 = Ok, anything < 0 = error. We never proceed
// on a non-zero status. IncompatibleStructVersion (-7) is the SAFE rejection the
// driver returns when our C# struct size ≠ NVIDIA's C struct — surfaced as a
// clean failure, never a crash.
// -----------------------------------------------------------------------
internal enum NvApiStatus
{
    Ok = 0,
    Error = -1,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    IncompatibleStructVersion = -7,
    NotSupported = -104,
    // 14/09/2026 — source : NVIDIA/nvapi, nvapi_lite_common.h
    // « NVAPI_EXECUTABLE_NOT_FOUND = -166, Application not found in the Profile. »
    ExecutableNotFound = -166,
}
