using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les protections de Windows contre les failles d'exécution spéculative du processeur (Spectre variante 2, Meltdown),
/// dites en information. Ce sont des protections de sécurité : DrDXT ne propose jamais de les couper, et ce constat n'a
/// ni geste ni remède. Lecture seule, sans droits particuliers.
/// </summary>
/// <remarks>
/// Source : le module PowerShell « SpeculationControl » de Microsoft (version 1.0.19, PowerShell Gallery), qui interroge
/// NtQuerySystemInformation avec la classe 201 (SystemSpeculationControlInformation) et la classe 196
/// (SystemKernelVaShadowInformation), et décode : classe 201, BpbEnabled 0x01, BpbDisabledSystemPolicy 0x02,
/// BpbDisabledNoHardwareSupport 0x04, SpecCtrlRetpolineEnabled 0x4000 ; classe 196, KvaShadowEnabled 0x01,
/// KvaShadowPcid 0x04, KvaShadowRequired 0x10, KvaShadowRequiredAvailable 0x20. Un appel refusé (classe inconnue de
/// cette version de Windows) rend « non lu ».
/// </remarks>
public static class AttenuationsProcesseur
{
    public sealed record Lu(uint? Speculation, uint? Kva);

    public static Lu Lire() => new(Classe(201), Classe(196));

    private static uint? Classe(int classe)
    {
        try
        {
            var tampon = new byte[8];
            return NtQuerySystemInformation(classe, tampon, tampon.Length, out _) == 0 ? BitConverter.ToUInt32(tampon, 0) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur.</summary>
    public static RepairFinding Juger(Lu lu)
    {
        if (lu.Speculation is null && lu.Kva is null)
            return new RepairFinding("SECURITE", "attenuations du processeur", null, null, RepairStatus.Unknown, Docteur.T("AP.NonLu"));
        var morceaux = new List<string>();
        if (lu.Speculation is { } s)
            morceaux.Add((s & 0x01) != 0 ? Docteur.T((s & 0x4000) != 0 ? "AP.BtiRetpoline" : "AP.BtiActive")
                : (s & 0x02) != 0 ? Docteur.T("AP.BtiStrategie")
                : (s & 0x04) != 0 ? Docteur.T("AP.BtiSansMateriel")
                : Docteur.T("AP.BtiInactive"));
        if (lu.Kva is { } k)
            morceaux.Add((k & 0x20) != 0 && (k & 0x10) == 0 ? Docteur.T("AP.KvaNonConcerne")
                : (k & 0x01) != 0 ? Docteur.T((k & 0x04) != 0 ? "AP.KvaActivePcid" : "AP.KvaActive")
                : Docteur.T("AP.KvaInactive"));
        return new RepairFinding("SECURITE", "attenuations du processeur", null, null, RepairStatus.Observed,
            string.Join(" ", morceaux) + Docteur.T("AP.JamaisCoupees"));
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, byte[] SystemInformation, int SystemInformationLength,
        out int ReturnLength);
}
