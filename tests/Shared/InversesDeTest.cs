using DrDxt.Core;

namespace DrDxt.Tests.Shared;

/// <summary>
/// Une annulation valide pour un levier, telle que son code l'écrit. Les fichiers d'annulation ne rendent plus
/// une entrée dont l'annulation n'est pas celle de son levier (RevertAuDemarrage.AnnulationSure) : un test qui passe
/// par eux porte donc une vraie annulation, pas un texte quelconque.
/// </summary>
internal static class InversesDeTest
{
    public static string Pour(string tweakId, string valeur = "1") => PrivilegedOpCodec.Serialize(tweakId switch
    {
        "gpu.power_limit" => new ApplyNvApiPowerLimitOp(0, int.Parse(valeur, System.Globalization.CultureInfo.InvariantCulture)),
        "gpu.undervolt" => new ApplyNvApiClockOffsetOp(0, int.Parse(valeur, System.Globalization.CultureInfo.InvariantCulture), 0),
        "gpu.repos_global_normal" => new ApplyNvApiDrsSettingOp(0x1057EB71, uint.Parse(valeur, System.Globalization.CultureInfo.InvariantCulture), "Base Profile"),
        "fixit.power_plan" => new SetActivePowerPlanOp(KnownPowerPlans.Balanced),
        "latency.hagsOn" => new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, valeur),
        "latency.reflex" => new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", RegistryValueType.DWord, valeur),
        "latency.vrrFlipModelOn" => new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.String, valeur),
        "latency.gameDvrBackgroundOff" => new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueType.DWord, valeur),
        _ => throw new ArgumentException($"Pas d'annulation de test pour {tweakId}.", nameof(tweakId)),
    }).Value!;
}
