namespace DrDxt.Core;

/// <summary>Le type de mémoire que le contrôleur AMD sait décoder ici. La LPDDR n'en fait pas partie.</summary>
public enum TypeMemoireAmd { Ddr4, Ddr5 }

/// <summary>Les timings qui tournent sur un canal, en cycles d'horloge mémoire, et le débit en MT/s.</summary>
public sealed record TimingsCanal(int Canal, int DebitMts, int Cl, int Rcd, int Rp, int Ras, int Rc, int Rfc,
    bool Commande2T, bool GearDown, bool PowerDown);

/// <summary>Les canaux actifs lus. <paramref name="DispositionVerifiee"/> est faux en DDR5 : la disposition des registres
/// n'a été confrontée à aucune lecture de référence.</summary>
public sealed record TimingsMemoire(TypeMemoireAmd Type, IReadOnlyList<TimingsCanal> Canaux, bool DispositionVerifiee);

/// <summary>
/// Les timings mémoire réellement appliqués par le contrôleur mémoire AMD (UMC), pas ceux que le BIOS dit avoir
/// demandés. Pur : décode des registres déjà lus. Les lire demande l'accès SMN, qui passe par un module PawnIO AMD que
/// DrDXT n'embarque pas : la lecture reste fermée et le constat dit « non lu » (GATES).
/// Sources : base des canaux, noyau Linux drivers/edac/amd64_edac.h (get_umc_base : 0x50000 + canal &lt;&lt; 20, commit
/// 6a857969) ; disposition des champs, compilation communautaire ZenStates-Core (github.com/irusanov/ZenStates-Core,
/// commit 869b7e82, Dictionaries/DDR4Dictionary.cs, DDR5Dictionary.cs, Hardware/DRAM/Ddr4Timings.cs, Ddr5Timings.cs),
/// lue comme documentation : AMD ne publie pas ces registres pour les puces de bureau. Non validé sur machine AMD réelle.
/// </summary>
public static class TimingsMemoireAmd
{
    /// <summary>Le registre du canal 0 ; celui du canal n est à + n &lt;&lt; 20 (amd64_edac.h, get_umc_base).</summary>
    public static uint Adresse(int canal, uint registreCanal0) => registreCanal0 + ((uint)canal << 20);

    /// <summary>Les registres du canal 0 que le décodage lit.</summary>
    public static readonly IReadOnlyList<uint> Registres = [0x5012C, 0x50200, 0x50204, 0x50208, 0x50260, 0x50264, 0x50268, 0x5026C];

    // SMBIOS Memory Device Type (SMBIOS 3.x, type 17) : 26 = DDR4, 34 = DDR5. Le reste (LPDDR, inconnu) n'est pas décodé.
    private const int SmbiosDdr4 = 26, SmbiosDdr5 = 34;

    // Les valeurs de repos que ZenStates-Core écarte dans les registres de tRFC (Ddr4Timings.cs, Ddr5Timings.cs) : un
    // registre qui les porte ne sert pas au rang actif.
    private const uint TrfcReposDdr4 = 0x21060138, TrfcReposDdr5 = 0x00C00138;

    /// <summary>Pur : le type si toutes les barrettes le partagent ; null pour un mélange, une LPDDR ou un type non lu.</summary>
    public static TypeMemoireAmd? Type(IReadOnlyList<int> typesSmbios)
    {
        if (typesSmbios.Count == 0) return null;
        if (typesSmbios.All(t => t == SmbiosDdr4)) return TypeMemoireAmd.Ddr4;
        if (typesSmbios.All(t => t == SmbiosDdr5)) return TypeMemoireAmd.Ddr5;
        return null;
    }

    /// <summary>
    /// Pur : un canal à partir de ses registres (clés : adresses du canal 0). Null si un registre manque ou si une valeur
    /// sort de sa bande de vraisemblance : une valeur hors bornes dit qu'on a lu autre chose qu'un timing.
    /// Débit : rapport d'horloge × 100 MHz × 2 (BaseDramTimings.cs), à une horloge de base de 100 MHz supposée.
    /// </summary>
    public static TimingsCanal? DecoderCanal(int canal, TypeMemoireAmd type, IReadOnlyDictionary<uint, uint> r)
    {
        if (!r.TryGetValue(0x50200, out var config) || !r.TryGetValue(0x50204, out var t1) || !r.TryGetValue(0x50208, out var t2)
            || !r.TryGetValue(0x5012C, out var energie))
            return null;
        var ddr5 = type == TypeMemoireAmd.Ddr5;
        // DDR4 : bits 6:0 = rapport × 3 ; DDR5 : bits 15:0 = rapport × 100.
        var debit = ddr5 ? (int)(config & 0xFFFF) * 2 : (int)Math.Round((config & 0x7F) * 200 / 3.0);
        var rfc = ddr5 ? TrfcDdr5(r) : TrfcDdr4(r);
        if (rfc is null) return null;
        var c = new TimingsCanal(canal, debit,
            Cl: (int)(t1 & 0x3F), Rcd: (int)((t1 >> 16) & 0x3F), Rp: (int)((t2 >> 16) & 0x3F), Ras: (int)((t1 >> 8) & 0x7F),
            Rc: (int)(t2 & 0xFF), Rfc: rfc.Value,
            Commande2T: Bit(config, ddr5 ? 17 : 10), GearDown: Bit(config, ddr5 ? 18 : 11), PowerDown: Bit(energie, 28));
        return Vraisemblable(c) ? c : null;
    }

    // DDR4 : deux registres ; s'ils diffèrent, celui qui ne porte pas la valeur de repos (bits 10:0).
    private static int? TrfcDdr4(IReadOnlyDictionary<uint, uint> r)
    {
        if (!r.TryGetValue(0x50260, out var a) || !r.TryGetValue(0x50264, out var b)) return null;
        var v = a != b && a == TrfcReposDdr4 ? b : a;
        return v == 0 ? null : (int)(v & 0x7FF);
    }

    // DDR5 : le premier des quatre registres qui n'est ni nul ni à la valeur de repos (bits 15:0).
    private static int? TrfcDdr5(IReadOnlyDictionary<uint, uint> r)
    {
        foreach (var adresse in new uint[] { 0x50260, 0x50264, 0x50268, 0x5026C })
            if (r.TryGetValue(adresse, out var v) && v != 0 && v != TrfcReposDdr5) return (int)(v & 0xFFFF);
        return null;
    }

    // Bandes de vraisemblance : un choix de DrDXT, larges (DDR4-1333 à DDR5-10000, CL 8 à 80), pas une norme.
    private static bool Vraisemblable(TimingsCanal c) =>
        c.DebitMts is >= 1333 and <= 10000 && c.Cl is >= 8 and <= 80 && c.Rcd is >= 8 and <= 100 && c.Rp is >= 8 and <= 100
        && c.Ras is >= 16 and <= 160 && c.Rc is >= 24 and <= 300 && c.Rfc is >= 50 and <= 2000;

    private static bool Bit(uint x, int i) => ((x >> i) & 1) == 1;

    /// <summary>Pur : tous les canaux actifs, ou null dès qu'un seul ne se décode pas (jamais une lecture partielle).</summary>
    public static TimingsMemoire? Decoder(TypeMemoireAmd type, IReadOnlyList<IReadOnlyDictionary<uint, uint>> canaux)
    {
        if (canaux.Count == 0) return null;
        var lus = new List<TimingsCanal>();
        for (var i = 0; i < canaux.Count; i++)
        {
            if (DecoderCanal(i, type, canaux[i]) is not { } c) return null;
            lus.Add(c);
        }
        return new TimingsMemoire(type, lus, DispositionVerifiee: type == TypeMemoireAmd.Ddr4);
    }

    /// <summary>Pur : le constat sur une machine AMD reconnue. <paramref name="canaux"/> null = registres non lus.</summary>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, IReadOnlyList<int> typesSmbios,
        IReadOnlyList<IReadOnlyDictionary<uint, uint>>? canaux)
    {
        if (generation is null) yield break;
        if (Type(typesSmbios) is not { } type)
        {
            yield return new RepairFinding("RAM", "timings appliques", null, null, RepairStatus.Unknown, Docteur.T("TMA.TypeNonDecode"));
            yield break;
        }
        if (canaux is null || Decoder(type, canaux) is not { } t)
        {
            yield return new RepairFinding("RAM", "timings appliques", null, null, RepairStatus.Unknown,
                Docteur.T(canaux is null ? "TMA.NonLus" : "TMA.Invraisemblables"));
            yield break;
        }
        var lignes = string.Join(" ; ", t.Canaux.Select(c =>
            $"{(char)('A' + c.Canal)} {c.DebitMts} MT/s {c.Cl}-{c.Rcd}-{c.Rp}-{c.Ras} tRC {c.Rc} tRFC {c.Rfc} {(c.Commande2T ? "2T" : "1T")}"
            + $" GDM {(c.GearDown ? "on" : "off")} PD {(c.PowerDown ? "on" : "off")}"));
        yield return new RepairFinding("RAM", "timings appliques", lignes, null, RepairStatus.Observed,
            Docteur.T(t.DispositionVerifiee ? "TMA.Lus" : "TMA.LusDdr5NonVerifiee", lignes));
    }
}
