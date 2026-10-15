using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>La limite de puissance du processeur lue par PawnIO (registres bruts + décodage, copie MMIO
/// comprise), pour l'état du scan et pour le levier. Sans PawnIO, sans Intel, sans droits : « non lu », jamais un chiffre.
/// Chaque source passe par une couture (registres directs, service, parties, microcode), pour
/// que le câblage des gardes se prouve sans la machine.</summary>
public sealed class PawnIoCpuPowerLimitReader : ICpuPowerLimitReader
{
    private readonly Func<(RegistresCpuLus? Registres, ulong? CopieMmio)> _direct;
    private readonly Func<EtatCpuServi?> _service;
    private readonly Func<IReadOnlyList<ChaleurEnPartie.PartieLue>> _parties;
    private readonly Func<(string? Nom, uint? Revision, string? Identifiant)> _microcode;
    private readonly Func<IReadOnlyList<TweakSnapshot>> _journal;

    public PawnIoCpuPowerLimitReader()
        : this(LireDirect,
            () => PawnIoIntel.Installe() && PawnIoIntel.ProcesseurIntel() ? ProcesseurParLeService.LireTout() : null,
            () => ChaleurEnPartie.Lire(DossierDonnees.Chemin("parties.json")),
            MicrocodeRaptorLake.Lire,
            () => new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(DrDxt.Application.Tweaks.JournalDesLeviersAppliques.CheminParDefaut()).Lire()) { }

    internal PawnIoCpuPowerLimitReader(Func<(RegistresCpuLus?, ulong?)> direct, Func<EtatCpuServi?> service,
        Func<IReadOnlyList<ChaleurEnPartie.PartieLue>> parties, Func<(string?, uint?, string?)> microcode,
        Func<IReadOnlyList<TweakSnapshot>>? journal = null)
        => (_direct, _service, _parties, _microcode, _journal) = (direct, service, parties, microcode, journal ?? (() => []));

    private static (RegistresCpuLus?, ulong?) LireDirect()
        => PawnIoIntel.LireRegistres() is { } d
            ? (new RegistresCpuLus(d.RaplPowerUnit, d.PkgPowerLimit, d.PkgPowerInfo), PawnIoIntel.LireCopieMmioLimite())
            : (null, null);

    public Result<LectureLimiteCpu> LireRegistres()
    {
        var (registres, copie) = _direct();
        // Sans droits, le service DrDXT sert les mêmes registres (s'il est installé et tourne).
        if (registres is null && _service() is { Limites: { } servis } lu0)
            (registres, copie) = (servis, lu0.CopieMmio);
        if (registres is null)
            return Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers not readable (PawnIO absent, not Intel, or no rights and no DrDXT service).");
        var lu = RegistreDeLimiteCpu.Decoder(registres.RaplPowerUnit, registres.PkgPowerLimit, registres.PkgPowerInfo);
        if (lu is null)
            return Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers are empty.");
        var parties = _parties();
        var (nom, revision, identifiant) = _microcode();
        lu = lu with
        {
            CopieMmioPl1W = RegistreDeLimiteCpu.CopieMmioPl1W(copie, registres.RaplPowerUnit),
            ChaleurEnPartieMaxC = ChaleurEnPartie.MaxC(parties),
            PlafondThermiqueW = ChaleurEnPartie.PlafondThermique(parties) ?? PlafondEncorePose(registres),
            MicrocodeVminPerime = MicrocodeVmin.Perime(nom, revision, identifiant) == true,
        };
        return Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(registres, lu));
    }

    /// <summary>La PL1 du plafond thermique quand il est encore posé dans le registre (même règle que le
    /// journal : seul le champ PL1 compte), sinon null. Sans elle, la partie freinée qui l'a demandé sortirait de la fenêtre du
    /// docteur dès qu'il marche, et la montée reviendrait par-dessus : une oscillation.</summary>
    private double? PlafondEncorePose(RegistresCpuLus registres)
    {
        var journal = _journal();
        return journal.FirstOrDefault(s => s.TweakId == "fixit.cpu_power_cap") is { AppliedValueJson: { } json } plafond
               && DrDxt.Application.Tweaks.FixIt.LevierPl1TweakBase.EncorePose(plafond, journal, registres.PkgPowerLimit) == true
               && PrivilegedOpCodec.Deserialize(json) is { IsOk: true, Value: SetCpuPackagePowerLimitOp pose }
               && RegistreDeLimiteCpu.Decoder(registres.RaplPowerUnit, pose.Valeur, registres.PkgPowerInfo) is { } lu
            ? lu.Pl1W : null;
    }

    /// <summary>La lecture décodée seule, pour l'état du scan ; null = non lu.</summary>
    public LimiteDePuissanceLue? Lire() => LireRegistres() is { IsOk: true, Value: { } v } ? v.Lue : null;
}
