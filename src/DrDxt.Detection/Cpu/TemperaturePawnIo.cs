using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// PAWN1 (09/10/2026) : la température du processeur lue dans ses propres registres (MSR) par PawnIO, quand PawnIO est
/// déjà installé sur la machine. DrDXT n'installe jamais PawnIO (un pilote noyau : c'est le choix du joueur, voir le guide) ;
/// sans lui, rien n'est lu et rien n'est dit.
///
/// Intel seulement pour l'instant : IA32_TEMPERATURE_TARGET (0x1A2, bits 23:16 = TjMax) et IA32_PACKAGE_THERM_STATUS
/// (0x1B1, bits 22:16 = écart sous TjMax). Température = TjMax − écart. Source : Intel SDM vol. 4. AMD : non lu (les
/// adresses SMN changent par famille ; sans machine pour les relever, « non lu » plutôt qu'un chiffre deviné).
///
/// B1 (10/10/2026) : la frontière native (modules, poignées, lecture et écriture) vit dans <see cref="PawnIoIntel"/>, dans
/// le projet natif commun, parce que le helper élevé écrit la limite de puissance par le même chemin. Ici ne restent que
/// les décodages vers les contrats du cœur.
/// </summary>
public static class TemperaturePawnIo
{
    public const string Libelle = "PawnIO (registres du processeur)";
    public const string Empreinte = PawnIoIntel.EmpreinteMsr;
    public const string EmpreinteMchbar = PawnIoIntel.EmpreinteMchbar;

    public static bool Installe() => PawnIoIntel.Installe();
    public static bool ProcesseurIntel() => PawnIoIntel.ProcesseurIntel();

    public static TemperatureHwinfo.Lecture? Lire()
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        var tjmax = PawnIoIntel.LireMsr(PawnIoIntel.MsrTemperatureTarget);
        var pkg = PawnIoIntel.LireMsr(PawnIoIntel.MsrPackageThermStatus);
        return tjmax is null || pkg is null ? null : Decoder(tjmax.Value, pkg.Value);
    }

    /// <summary>J-MECA (09/10/2026) : PL1, PL2 et la puissance de base du processeur Intel, lus dans ses registres. Mêmes
    /// conditions que la température : PawnIO installé, processeur Intel ; sinon null. B1 : avec la copie MMIO de la carte
    /// mère et la puissance maximale publiée par le processeur, pour que le levier sache s'il peut monter.</summary>
    public static DrDxt.Core.LimiteDePuissanceLue? LireLimites() => new PawnIoCpuPowerLimitReader().Lire();

    /// <summary>Le pur vit dans le cœur (<see cref="DrDxt.Core.RegistreDeLimiteCpu"/>) ; gardé ici pour les appelants.</summary>
    public static DrDxt.Core.LimiteDePuissanceLue? DecoderLimites(ulong raplPowerUnit, ulong pkgPowerLimit, ulong pkgPowerInfo)
        => DrDxt.Core.RegistreDeLimiteCpu.Decoder(raplPowerUnit, pkgPowerLimit, pkgPowerInfo);

    public static double? CopieMmioPl1W(ulong? copie, ulong raplPowerUnit) => DrDxt.Core.RegistreDeLimiteCpu.CopieMmioPl1W(copie, raplPowerUnit);

    /// <summary>Pur : TjMax (0x1A2) et l'état thermique du paquet (0x1B1) ⇒ la température, ou null si les registres ne
    /// disent rien de plausible (TjMax hors 50-120 °C, ou écart au-dessus de TjMax).</summary>
    public static TemperatureHwinfo.Lecture? Decoder(ulong temperatureTarget, ulong packageThermStatus)
    {
        var tjmax = (int)((temperatureTarget >> 16) & 0xFF);
        var ecart = (int)((packageThermStatus >> 16) & 0x7F);
        if (tjmax is < 50 or > 120 || ecart > tjmax) return null;
        return new TemperatureHwinfo.Lecture(tjmax - ecart, Libelle);
    }
}
