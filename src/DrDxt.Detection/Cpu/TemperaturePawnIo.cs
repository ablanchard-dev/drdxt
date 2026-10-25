using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// La température du processeur lue dans ses propres registres (MSR) par PawnIO, quand PawnIO est
/// déjà installé sur la machine. DrDXT n'installe jamais PawnIO (un pilote noyau : c'est le choix du joueur, voir le guide) ;
/// sans lui, rien n'est lu et rien n'est dit.
///
/// Intel seulement pour l'instant : IA32_TEMPERATURE_TARGET (0x1A2, bits 23:16 = TjMax) et IA32_PACKAGE_THERM_STATUS
/// (0x1B1, bits 22:16 = écart sous TjMax). Température = TjMax − écart. Source : Intel SDM vol. 4. AMD : non lu (les
/// adresses SMN changent par famille ; sans machine pour les relever, « non lu » plutôt qu'un chiffre deviné).
///
/// La frontière native (modules, poignées, lecture et écriture) vit dans <see cref="PawnIoIntel"/>, dans
/// le projet natif commun, parce que le helper élevé écrit la limite de puissance par le même chemin. Ici ne restent que
/// les décodages vers les contrats du cœur.
/// </summary>
public static class TemperaturePawnIo
{
    public static string Libelle => DrDxt.Core.Docteur.T("TP.Libelle");
    public const string Empreinte = PawnIoIntel.EmpreinteMsr;
    public const string EmpreinteMchbar = PawnIoIntel.EmpreinteMchbar;

    public static bool Installe() => PawnIoIntel.Installe();
    public static bool ProcesseurIntel() => PawnIoIntel.ProcesseurIntel();

    public static TemperatureHwinfo.Lecture? Lire() => LireDirecte() ?? LireParLeService();

    /// <summary>Les registres lus ici : seulement élevé (sans droits, PawnIO refuse à chaque fois, on ne le
    /// retente pas chaque seconde).</summary>
    public static TemperatureHwinfo.Lecture? LireDirecte()
    {
        if (!Installe() || !ProcesseurIntel() || !Eleve.Value) return null;
        var tjmax = PawnIoIntel.LireMsr(PawnIoIntel.MsrTemperatureTarget);
        var pkg = PawnIoIntel.LireMsr(PawnIoIntel.MsrPackageThermStatus);
        return tjmax is null || pkg is null ? null : Decoder(tjmax.Value, pkg.Value);
    }

    /// <summary>Les mêmes registres, servis par le service DrDXT (sans droits, ou quand la lecture directe a raté).</summary>
    public static TemperatureHwinfo.Lecture? LireParLeService()
        => Installe() && ProcesseurIntel() && ProcesseurParLeService.Lire() is { } e ? new TemperatureHwinfo.Lecture(e.ChaudC, Libelle) : null;

    /// <summary>En administrateur, la lecture directe ; sans droits, le service DrDXT la sert (s'il est
    /// installé et tourne). Avec le ventilateur CPU de la même seconde, quand la carte le laisse lire.</summary>
    public static DrDxt.Core.EtatCpu? LireEtat()
    {
        // La puce de la carte d'abord (régime et cycle), sinon les ventilateurs que HWiNFO publie.
        if (Eleve.Value)
            return AvecVentilo(LireEtatDirect(), PawnIoSuperIo.LireRecent()?.Ventilateurs.Select(v => new DrDxt.Core.VentilateurLu(v.Index, v.TrMin, v.PwmPourcent)).ToList()
                                                 ?? TemperatureHwinfo.LireVentilateurs());
        return StatutsPresents.Value && Installe() && ProcesseurIntel() && ProcesseurParLeService.LireTout() is { } servi
            ? AvecVentilo(servi.Etat, servi.Ventilateurs ?? TemperatureHwinfo.LireVentilateurs()) : null;
    }

    /// <summary>Pur : la seconde, avec tous les ventilateurs lus, et celui du processeur seulement quand il est sûr : l'en-tête 0
    /// d'une puce lue, avec son cycle (un ventilateur HWiNFO sans cycle ne juge jamais la courbe du processeur).</summary>
    public static DrDxt.Core.EtatCpu? AvecVentilo(DrDxt.Core.EtatCpu? etat, IReadOnlyList<DrDxt.Core.VentilateurLu>? ventilateurs)
        => etat is null ? null : ventilateurs is null ? etat
            : etat with { VentiloCpu = ventilateurs.FirstOrDefault(v => v.Index == 0 && v.PwmPourcent is not null), Ventilateurs = ventilateurs };

    private static readonly Lazy<bool> Eleve = new(() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));

    /// <summary>La seconde du processeur entière (cœur le plus chaud, cœur 0, raisons de freiner), ou null, lue dans CE
    /// processus sans le service (c'est celle que le service lui-même fait). Sans les statuts de limite et de chaleur du
    /// paquet (CPUID.06H:EAX bits 4 et 6), les bits liraient 0 et diraient « jamais freiné » : rien n'est lu.</summary>
    public static DrDxt.Core.EtatCpu? LireEtatDirect()
    {
        if (!StatutsPresents.Value || !Installe() || !ProcesseurIntel()) return null;
        var tjmax = PawnIoIntel.LireMsr(PawnIoIntel.MsrTemperatureTarget);
        var pkg = PawnIoIntel.LireMsr(PawnIoIntel.MsrPackageThermStatus);
        return tjmax is null || pkg is null ? null
            : DrDxt.Core.EtatCpu.Decoder(tjmax.Value, pkg.Value, PawnIoIntel.LireMsrPremierCoeur(PawnIoIntel.MsrThermStatus));
    }

    private static readonly Lazy<bool> StatutsPresents = new(() =>
        System.Runtime.Intrinsics.X86.X86Base.IsSupported
        && (System.Runtime.Intrinsics.X86.X86Base.CpuId(6, 0).Eax & 0x50) == 0x50);

    /// <summary>PL1, PL2 et la puissance de base du processeur Intel, lus dans ses registres. Mêmes
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
        => DrDxt.Core.EtatCpu.Decoder(temperatureTarget, packageThermStatus, null) is { } e
            ? new TemperatureHwinfo.Lecture(e.ChaudC, Libelle) : null;
}
