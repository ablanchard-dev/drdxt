namespace DrDxt.Core;

/// <summary>
/// Les saccades liées au TPM du processeur (fTPM) sur certains Ryzen. AMD, article PA-410 « Intermittent System Stutter
/// Experienced with fTPM Enabled on Windows 10 and 11 » (relevé le 16/10/2026) : des transactions fTPM longues dans la
/// mémoire flash SPI de la carte figent le PC un instant ; le correctif vient par un BIOS « based on AMD AGESA 1207 (or
/// newer) », disponible « starting in early May, 2022 ». DrDXT ne lit pas l'AGESA : il compare la date du BIOS et dit
/// « à vérifier », jamais « touché ». Lecture seule, sans appel de méthode du firmware.
/// </summary>
public static class FtpmAmd
{
    /// <summary>Avant cette date, aucun BIOS ne pouvait porter le correctif (PA-410 : « early May, 2022 »).</summary>
    public static readonly DateTime Correctif = new(2022, 5, 1);

    /// <summary>Générations dont le BIOS porte une AGESA de la lignée 1.2.0.x (AGESA 1207 de l'article).</summary>
    private static readonly HashSet<string> Concernees = ["Zen / Zen+", "Zen 2", "Zen 3"];

    /// <summary>Identifiant du fabricant du TPM d'AMD (« AMD » en ASCII, registre des identifiants de fabricants du TCG).</summary>
    public const uint IdentifiantAmd = 0x414D4400;

    /// <param name="generation">Le portail CPUID ; null = non reconnue, et rien n'est dit.</param>
    /// <param name="fabricantTpm">L'identifiant du fabricant du TPM actif ; null = TPM non lu.</param>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, uint? fabricantTpm, bool tpmLu, DateTime? dateBios)
    {
        if (generation is null || !Concernees.Contains(generation.Generation)) yield break;
        if (!tpmLu)
        {
            yield return new RepairFinding("BIOS", "fTPM AMD", null, null, RepairStatus.Unknown, Docteur.T("FT.TpmNonLu"));
            yield break;
        }
        // Un TPM d'un autre fabricant (module dTPM, ou aucun) n'est pas le fTPM de l'article.
        if (fabricantTpm != IdentifiantAmd) yield break;
        var source = Docteur.T("FT.Source");
        if (dateBios is not { } d)
            yield return new RepairFinding("BIOS", "fTPM AMD", null, null, RepairStatus.Unknown, Docteur.T("FT.DateNonLue") + source);
        else if (d < Correctif)
            yield return new RepairFinding("BIOS", "fTPM AMD", d.ToString(Docteur.T("Format.Date"), System.Globalization.CultureInfo.InvariantCulture),
                Docteur.T("FT.Attendu"), RepairStatus.Suspicious, Docteur.T("FT.Ancien", d) + source, RepairAction.Recommandation);
        else
            yield return new RepairFinding("BIOS", "fTPM AMD", d.ToString(Docteur.T("Format.Date"), System.Globalization.CultureInfo.InvariantCulture),
                Docteur.T("FT.Attendu"), RepairStatus.Observed, Docteur.T("FT.Recent", d) + source);
    }
}
