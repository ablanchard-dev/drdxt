namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiStatus : le sous-ensemble des codes NvAPI_Status sur lesquels on aiguille.
//
// Règle d'or de sécurité n°3 (recette) : 0 = Ok, tout < 0 = erreur. On ne continue jamais sur un
// statut non nul. IncompatibleStructVersion (-7) est le rejet sûr que rend le pilote quand la
// taille de notre struct C# ≠ celle de la struct C de NVIDIA : remonté en échec propre, jamais en plantage.
// -----------------------------------------------------------------------
internal enum NvApiStatus
{
    Ok = 0,
    Error = -1,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    IncompatibleStructVersion = -7,
    NotSupported = -104,
    // Source : NVIDIA/nvapi, nvapi_lite_common.h
    // « NVAPI_EXECUTABLE_NOT_FOUND = -166, Application not found in the Profile. »
    ExecutableNotFound = -166,
}
