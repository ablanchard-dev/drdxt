namespace DrDxt.Core;

/// <summary>Les trois registres bruts de la limite de puissance d'un processeur Intel : unités (MSR 0x606), limite
/// (0x610) et puissances publiées (0x614). Lus par PawnIO ; gardés tels quels pour rendre l'origine exactement.</summary>
public sealed record RegistresCpuLus(ulong RaplPowerUnit, ulong PkgPowerLimit, ulong PkgPowerInfo);

/// <summary>Ce que le lecteur de limite rend au levier : les registres bruts et leur décodage.</summary>
public sealed record LectureLimiteCpu(RegistresCpuLus Registres, LimiteDePuissanceLue Lue);

/// <summary>
/// Le pur du registre MSR_PKG_POWER_LIMIT (Intel SDM vol. 4) : 0x606 bits 3:0 = unité de puissance
/// (1/2^n W) ; 0x610 bits 14:0 = PL1, 15 = PL1 activée, 23:17 = fenêtre, 46:32 = PL2, 47 = PL2 activée, 63 = verrou ;
/// 0x614 bits 14:0 = puissance de base, 46:32 = puissance maximale. Même format pour la copie MMIO (MCHBAR + 0x59A0).
/// Partagé par la détection (décoder), le levier (composer) et le helper (admettre) : une seule arithmétique.
/// </summary>
public static class RegistreDeLimiteCpu
{
    public static double UniteWatts(ulong raplPowerUnit) => 1.0 / (1 << (int)(raplPowerUnit & 0xF));

    /// <summary>Une PL1 nulle ou une base nulle : registres vides, null. Une PL1 au maximum du champ (0x7FFF) n'est pas
    /// Une lecture ratée : c'est la carte qui dit « sans limite ».</summary>
    public static LimiteDePuissanceLue? Decoder(ulong raplPowerUnit, ulong pkgPowerLimit, ulong pkgPowerInfo)
    {
        var unite = UniteWatts(raplPowerUnit);
        var pl1Brut = pkgPowerLimit & 0x7FFF;
        var pl1 = pl1Brut * unite;
        var pl2 = ((pkgPowerLimit >> 32) & 0x7FFF) * unite;
        var nominale = (pkgPowerInfo & 0x7FFF) * unite;
        if (pl1 <= 0 || nominale <= 0 || nominale > 2000) return null;
        return new LimiteDePuissanceLue(pl1, pl2, nominale, ((pkgPowerLimit >> 63) & 1) == 1,
            Pl1Active: ((pkgPowerLimit >> 15) & 1) == 1, RegistreAuMaximum: pl1Brut == 0x7FFF,
            MaximaleW: ((pkgPowerInfo >> 32) & 0x7FFF) * unite);
    }

    /// <summary>La copie MMIO décodée en watts : null = non lue ; 0 = présente mais inactive (bit 15 à zéro ou champ vide :
    /// la carte ne tient rien par là, mesuré sur la machine de référence) ; sinon la PL1 que la carte tient.</summary>
    public static double? CopieMmioPl1W(ulong? copie, ulong raplPowerUnit)
    {
        if (copie is not { } c) return null;
        var champ = c & 0x7FFF;
        if (champ == 0 || ((c >> 15) & 1) == 0) return 0;
        return champ * UniteWatts(raplPowerUnit);
    }

    /// <summary>La valeur de 0x610 avec seulement le champ PL1 (bits 14:0) remplacé par <paramref name="pl1W"/> dans
    /// l'unité de 0x606, et PL1 activée (bit 15). PL2, fenêtres, bride, verrou : intouchés. Borné à 0x7FFF, jamais zéro.</summary>
    public static ulong ComposerPl1(ulong pkgPowerLimit, double pl1W, ulong raplPowerUnit)
    {
        var champ = (ulong)Math.Clamp(Math.Round(pl1W / UniteWatts(raplPowerUnit)), 1, 0x7FFF);
        return (pkgPowerLimit & ~0x7FFFUL) | champ | (1UL << 15);
    }

    /// <summary>Vrai si <paramref name="valeur"/> ne diffère de <paramref name="autre"/> que par PL1 et son bit d'activation
    /// (bits 15:0), sans verrou, avec une PL1 d'au moins 1 et au plus la PL2 du registre. La seule forme d'écriture que le
    /// helper accepte, dans les deux sens (pose et retour).</summary>
    public static bool NeToucheQuePl1(ulong valeur, ulong autre)
    {
        if ((valeur & ~0xFFFFUL) != (autre & ~0xFFFFUL)) return false;
        if (((valeur >> 63) & 1) == 1) return false;
        var pl1 = valeur & 0x7FFF;
        var pl2 = (valeur >> 32) & 0x7FFF;
        return pl1 >= 1 && pl2 >= 1 && pl1 <= pl2;
    }

    public static bool Verrou(ulong pkgPowerLimit) => ((pkgPowerLimit >> 63) & 1) == 1;
}
