using System.Globalization;
using DrDxt.Core;

namespace DrDxt.AmdHarness.Reporting;

/// <summary>Ce que « --lire » affiche d'un Ryzen par PawnIO, en lecture seule : la température (Tctl et chaque CCD), la
/// cause matérielle des redémarrages, l'identité du SMU et les registres de timings bruts de chaque canal, pour qu'un
/// testeur mette chaque valeur à côté de HWiNFO et de ZenTimings. Pur : met en forme des lectures déjà faites.</summary>
internal static class LecturePawnIoAmd
{
    /// <param name="canaux">Les registres de timings (<see cref="TimingsMemoireAmd.Registres"/>) de chaque canal actif, sous son numéro physique.</param>
    public static IReadOnlyList<string> Lignes(bool pawnIoInstalle, GenerationAmd.Ligne? generation, double? tctl,
        IReadOnlyList<double>? ccd, uint? s5, (uint NomDeCode, uint VersionSmu)? smu, IReadOnlyList<(int Canal, IReadOnlyDictionary<uint, uint> Registres)>? canaux)
    {
        if (!pawnIoInstalle) return ["PawnIO : absent, rien n'est lu sur le processeur (DrDXT ne l'installe jamais)"];
        var c = CultureInfo.InvariantCulture;
        var lignes = new List<string>
        {
            "PawnIO : présent, lecture seule",
            $"Génération    : {generation?.Generation ?? "non reconnue"}",
            $"Tctl          : {(tctl is { } t ? t.ToString("0.0", c) + " °C" : "non lue")}",
            $"CCD           : {(ccd is { Count: > 0 } l ? string.Join(" ; ", l.Select(v => v.ToString("0.0", c) + " °C")) : "non lus")}",
            $"S5 (reset)    : {S5(s5)}",
            $"SMU           : {(smu is { } s ? $"version 0x{s.VersionSmu:X8}, nom de code {s.NomDeCode}" : "non lu")}",
        };
        if (canaux is null) lignes.Add("Timings       : non lus");
        else if (canaux.Count == 0) lignes.Add("Timings       : aucun canal actif");
        else
            foreach (var (canal, registres) in canaux)
                lignes.Add($"Timings canal {canal} : " + string.Join(" ", TimingsMemoireAmd.Registres.Select(r => $"{r:X5}={registres[r]:X8}")));
        return lignes;
    }

    private static string S5(uint? valeur) => valeur is not { } v ? "non lu"
        : ResetAmd.Bits(v) is not { } bits ? "non lu (tous les bits à 1)"
        : $"0x{v:X8}, " + (bits.Count == 0 ? "aucune cause" : "bits " + string.Join(", ", bits));
}
