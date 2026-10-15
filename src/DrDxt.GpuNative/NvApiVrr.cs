namespace DrDxt.GpuNative.NvApi;

/// <summary>
/// Ce que le pilote NVIDIA dit du VRR (G-Sync) d'un ecran : `NV_GET_VRR_INFO_V1`.
///
/// Win32 ne voit pas G-Sync : sans cette lecture, `SupportsVrr` resterait a false, et le seul levier
/// de cadence (Vsync + cap sous VRR) serait mort sur toute machine NVIDIA.
///
/// Source : docs.nvidia.com/nvapi, struct _NV_GET_VRR_INFO_V1 : `NvU32 version` puis un mot de bits
/// dans cet ordre : bIsVRREnabled:1, bIsVRRPossible:1, bIsVRRRequested:1, bIsVRRIndicatorEnabled:1,
/// bIsDisplayInVRRMode:1, reserved:27 ; puis `NvU32 reservedEx[4]`. Bitfields MSVC : bit de poids
/// faible en premier.
/// </summary>
/// <param name="Possible">L'ecran et la chaine d'affichage savent faire du VRR.</param>
/// <param name="Enabled">G-Sync est active pour cet ecran (reglage du pilote).</param>
/// <param name="Requested">Une application le demande en ce moment.</param>
/// <param name="InVrrMode">L'ecran est en mode VRR a l'instant de la lecture (jeu plein ecran).</param>
public readonly record struct VrrReading(bool Possible, bool Enabled, bool Requested, bool InVrrMode)
{
    public static VrrReading Decoder(uint bits) => new(
        Possible: (bits & 0b0_0010) != 0,
        Enabled: (bits & 0b0_0001) != 0,
        Requested: (bits & 0b0_0100) != 0,
        InVrrMode: (bits & 0b1_0000) != 0);
}
