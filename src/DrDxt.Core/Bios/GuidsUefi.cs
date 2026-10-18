namespace DrDxt.Core;

/// <summary>
/// Les GUID des variables firmware que DrDXT lit, en un seul endroit : la liste blanche du helper (qui autorise la
/// lecture) et le lecteur du BIOS (qui la demande) citent les mêmes constantes. Deux copies du même GUID pourraient
/// diverger, et la lecture serait alors refusée sans qu'aucun test ne le voie. Les sources de chaque GUID sont dites
/// dans la liste blanche (<c>OpAllowlist</c>).
/// </summary>
public static class GuidsUefi
{
    /// <summary>Le varstore de configuration AMI, où vit « Setup ».</summary>
    public const string AmiSetup = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";

    /// <summary>Le varstore « CpuSetup » de la référence Intel.</summary>
    public const string IntelCpuSetup = "{b08f97ff-e6e8-4193-a997-5e9e9b0adb32}";

    /// <summary>Le magasin des valeurs d'usine d'AMI (« StdDefaults »).</summary>
    public const string AmiStandardDefaults = "{4599d26f-1a11-49b8-b91f-858745cff824}";
}
