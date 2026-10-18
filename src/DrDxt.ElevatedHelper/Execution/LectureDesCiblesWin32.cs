using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;
using DrDxt.Reconciler.Desinstallation;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>La lecture de la valeur courante, pour la desinstallation : registre, plan actif,
/// etat d'un service, reglage DRS du profil de base. Lecture seule ; tout echec rend <c>Fail</c> (illisible
/// ⇒ rien n'est touche), jamais une exception.
/// Simplification voulue : double volontairement les lecteurs bruts de DrDxt.Reconciler.Adapters, qui tirent Detection
/// (et ses dependances) : le helper eleve reste sans Detection.</summary>
[SupportedOSPlatform("windows")]
public sealed class LectureDesCiblesWin32 : ILectureDesCibles
{
    private readonly Lazy<INvApiNative> _nvApi;
    private readonly Lazy<DrDxt.Detection.Gpu.Adlx.IAdlxNative> _adlx;

    /// <param name="nvApi">Absent : le vrai NVAPI, cree a la premiere lecture DRS.</param>
    /// <param name="adlx">Le pilote AMD, le même que celui qui écrit (un seul ADLXInitialize par processus).</param>
    public LectureDesCiblesWin32(INvApiNative? nvApi = null, DrDxt.Detection.Gpu.Adlx.IAdlxNative? adlx = null)
    {
        _nvApi = new(() => nvApi ?? new NvApiNative());
        _adlx = new(() => adlx ?? new DrDxt.Detection.Gpu.Adlx.AdlxNative());
    }

    public Result<string?> Lire(PrivilegedOp ecriture)
    {
        try
        {
            return Lecteur(ecriture) is { } lire ? lire() : Illisible(Docteur.T("LC.AucuneLecture", ecriture.GetType().Name));
        }
        catch (Exception ex)
        {
            return Illisible(ex.Message);
        }
    }

    /// <summary>La lecture qui sait relire la cible de cette op, sans la faire ; <c>null</c> quand aucune ne sait. Le journal
    /// d'écriture anticipée ne journalise que ce qui a une lecture (ClassesDuJournal).</summary>
    internal Func<Result<string?>>? Lecteur(PrivilegedOp ecriture) => ecriture switch
    {
        SetRegistryValueOp s => () => LireRegistre(s),
        SetActivePowerPlanOp => LirePlanActif,
        SetProcessorBoostModeOp b => () => LireModeDeBoost(b.PlanGuid),
        SetWirelessPowerSavingOp w => () => LireValeurDePlanAc(w.PlanGuid, EconomieSansFilLue.SousGroupe, EconomieSansFilLue.Reglage),
        // Le registre entier, en décimal, comme l'op le pose.
        SetCpuPackagePowerLimitOp => () => DrDxt.GpuNative.PawnIo.PawnIoIntel.LireRegistres() is { } r
            ? Result<string?>.Ok(r.PkgPowerLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : Result<string?>.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register not readable."),
        RunServiceControlOp r => () => LireService(r.ServiceName),
        ApplyNvApiDrsSettingOp d when OpAllowlist.IsBaseProfileScope(d.ProfileName) => () => LireDrs(d.SettingId),
        ApplyNvApiDrsSettingOp d when OpAllowlist.IsQualifiedExecutablePath(d.ProfileName)
            => () => LireDrsAuJeu(d.ProfileName, d.SettingId),
        // Les réglages de la carte, relus sous la forme que leur op pose (un entier).
        ApplyNvApiPowerLimitOp n => () => _nvApi.Value.IsAvailable && _nvApi.Value.TryReadPowerLimitPct(n.GpuIndex, out var pct)
            ? Result<string?>.Ok(pct.ToString(CultureInfo.InvariantCulture))
            : Illisible(Docteur.T("LC.LimiteNvidia")),
        // Les genres que LireAdlx relit ; la sous-tension et la remise à l'usine n'ont pas encore de lecture.
        ApplyAdlxTuningOp a when a.Kind is AdlxTuningKind.PowerLimit or AdlxTuningKind.AntiLag
            or AdlxTuningKind.FrameRateTarget or AdlxTuningKind.VerticalRefresh => () => LireAdlx(a),
        _ => null,
    };

    private Result<string?> LireAdlx(ApplyAdlxTuningOp a)
    {
        if (!_adlx.Value.IsAvailable) return Illisible(Docteur.T("LC.AdlxAbsent"));
        int? lu = a.Kind switch
        {
            AdlxTuningKind.PowerLimit => _adlx.Value.TryReadPower(a.GpuIndex, out var p) ? p.PowerLimitPct : null,
            AdlxTuningKind.AntiLag => _adlx.Value.LireReglages().AntiLag is { } on ? (on ? 1 : 0) : null,
            AdlxTuningKind.FrameRateTarget => _adlx.Value.LireReglages().FrtcCode,
            AdlxTuningKind.VerticalRefresh => _adlx.Value.LireReglages().VsyncMode,
            _ => null,
        };
        return lu is { } v ? Result<string?>.Ok(v.ToString(CultureInfo.InvariantCulture)) : Illisible(Docteur.T("LC.ReglageAmd", a.Kind));
    }

    // NVDRS_SETTING_LOCATION : la valeur appartient au profil lu (DrsSettingOrigin, PG-12072-001).
    private const uint EmplacementProfilCourant = 0;

    /// <summary>Un reglage pose au profil d'un jeu (PreferMaxPerf, RenderQueueCap, VrrVsyncCap).
    /// Sans ce cas il serait « illisible » et la desinstallation ne le rendrait jamais.
    /// Un reglage que le jeu ne possede plus se relit a la valeur heritee du global, qui peut valoir
    /// exactement ce que DrDXT avait pose : il est lu absent, sinon on rendrait un reglage disparu.</summary>
    private Result<string?> LireDrsAuJeu(string jeu, uint reglage)
    {
        if (!_nvApi.Value.IsAvailable || !_nvApi.Value.TryReadDrsSettingForApplication(jeu, reglage, out var lu))
            return Illisible(Docteur.T("LC.DrsJeu", $"0x{reglage:X8}"));
        return Result<string?>.Ok(lu.SettingLocation == EmplacementProfilCourant
            ? lu.Value.ToString(CultureInfo.InvariantCulture)
            : null);
    }

    /// <summary>La cible existe-t-elle encore ? On interroge le contenant, pas la valeur :
    /// la cle de registre, le service, le plan d'alimentation reference. Une valeur absente d'une cle
    /// qui existe n'est pas une cible disparue (c'est la situation normale d'un DeleteRegistryValueOp).
    /// Inconnu ⇒ Fail : on ne touche pas non plus.</summary>
    public Result<bool> CibleExiste(PrivilegedOp ecriture)
    {
        try
        {
            return ecriture switch
            {
                SetRegistryValueOp s => CleExiste(s.Hive, s.Path),
                DeleteRegistryValueOp d => CleExiste(d.Hive, d.Path),
                // Le plan d'alimentation vise par le retour : s'il a ete efface, l'activer echouerait
                // (ou, pire, on ecrirait un GUID qui ne designe plus rien).
                SetActivePowerPlanOp p => PlanExiste(p.PlanGuid),
                SetProcessorBoostModeOp b => PlanExiste(b.PlanGuid),
                SetWirelessPowerSavingOp w => PlanExiste(w.PlanGuid),
                // La cible existe tant que PawnIO est là et que le processeur est Intel.
                SetCpuPackagePowerLimitOp => Result<bool>.Ok(DrDxt.GpuNative.PawnIo.PawnIoIntel.Installe() && DrDxt.GpuNative.PawnIo.PawnIoIntel.ProcesseurIntel()),
                RunServiceControlOp r => ServiceExiste(r.ServiceName),
                // NVAPI : le pilote doit repondre. Absent ⇒ la cible n'existe plus sur cette machine.
                ApplyNvApiDrsSettingOp or DeleteNvApiDrsSettingOp => Result<bool>.Ok(_nvApi.Value.IsAvailable),
                // La carte doit répondre ; pilote absent (carte changée) = la cible n'existe plus.
                ApplyNvApiPowerLimitOp => Result<bool>.Ok(_nvApi.Value.IsAvailable),
                ApplyAdlxTuningOp => Result<bool>.Ok(_adlx.Value.IsAvailable),
                _ => Result<bool>.Fail(ErrorCodes.IoFailure,
                    $"aucune sonde d'existence pour {ecriture.GetType().Name}"),
            };
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }

    private static Result<bool> CleExiste(RegistryHiveKind ruche, string chemin)
    {
        var racine = ruche switch
        {
            RegistryHiveKind.LocalMachine => Registry.LocalMachine,
            RegistryHiveKind.CurrentUser => Registry.CurrentUser,
            _ => null,
        };
        if (racine is null) return Result<bool>.Fail(ErrorCodes.IoFailure, Docteur.T("LC.Ruche", ruche));
        using var cle = racine.OpenSubKey(chemin, writable: false);
        return Result<bool>.Ok(cle is not null);
    }

    private static Result<bool> PlanExiste(Guid plan)
    {
        uint taille = 0;
        var rc = PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref taille);
        // ERROR_FILE_NOT_FOUND (2) : le plan a ete efface. 0 ou ERROR_MORE_DATA (234) : il repond.
        return rc switch
        {
            0 or 234 => Result<bool>.Ok(true),
            2 => Result<bool>.Ok(false),
            _ => Result<bool>.Fail(ErrorCodes.IoFailure, $"PowerReadFriendlyName rc={rc}"),
        };
    }

    private static Result<bool> ServiceExiste(string nom)
    {
        using var service = new ServiceController(nom);
        try
        {
            _ = service.Status;
            return Result<bool>.Ok(true);
        }
        catch (InvalidOperationException)
        {
            return Result<bool>.Ok(false); // service non installe.
        }
    }

    private static Result<string?> LireRegistre(SetRegistryValueOp s)
    {
        var racine = s.Hive switch
        {
            RegistryHiveKind.LocalMachine => Registry.LocalMachine,
            RegistryHiveKind.CurrentUser => Registry.CurrentUser,
            _ => null,
        };
        if (racine is null) return Illisible(Docteur.T("LC.Ruche", s.Hive));

        using var cle = racine.OpenSubKey(s.Path, writable: false);
        var brute = cle?.GetValue(s.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (brute is null) return Result<string?>.Ok(null);
        // .NET rend une chaîne pour REG_SZ comme pour REG_EXPAND_SZ : le type se lit à part.
        return TypeConforme(s.Kind, cle!.GetValueKind(s.Name)) ? Forme(s.Kind, s.Name, brute) : Illisible(Docteur.T("LC.Type", s.Name, s.Kind));
    }

    /// <summary>Le type de la valeur en place est-il celui que l'op pose ? Sinon l'origine, réécrite sous le type posé,
    /// changerait de type (un REG_EXPAND_SZ deviendrait un REG_SZ) : elle est illisible, rien ne se rejoue.</summary>
    internal static bool TypeConforme(RegistryValueType pose, RegistryValueKind lu) => (pose, lu) switch
    {
        (RegistryValueType.String, RegistryValueKind.String) => true,
        (RegistryValueType.DWord, RegistryValueKind.DWord) => true,
        (RegistryValueType.QWord, RegistryValueKind.QWord) => true,
        (RegistryValueType.MultiString, RegistryValueKind.MultiString) => true,
        _ => false,
    };

    /// <summary>La valeur brute du registre, sous la forme que l'op la pose (ValeurPosee du rendu).</summary>
    internal static Result<string?> Forme(RegistryValueType type, string nom, object brute) => (type, brute) switch
    {
        (RegistryValueType.DWord, int i) => Result<string?>.Ok(unchecked((uint)i).ToString(CultureInfo.InvariantCulture)),
        (RegistryValueType.QWord, long l) => Result<string?>.Ok(unchecked((ulong)l).ToString(CultureInfo.InvariantCulture)),
        (RegistryValueType.String, string texte) => Result<string?>.Ok(texte),
        (RegistryValueType.MultiString, string[] entrees) => Result<string?>.Ok(MultiTexteDuRegistre.Canonique(entrees)),
        _ => Illisible(Docteur.T("LC.Type", nom, type)),
    };

    private static Result<string?> LirePlanActif()
    {
        var (plan, rc, exception) = PlanActif.Lire();
        return plan is { } guid
            ? Result<string?>.Ok(guid.ToString("D", CultureInfo.InvariantCulture))
            : Illisible(exception ?? $"PowerGetActiveScheme rc={rc}");
    }

    /// <summary>PERFBOOSTMODE sur secteur dans ce plan (PowerReadACValueIndex, lecture seule).</summary>
    private static Result<string?> LireModeDeBoost(Guid plan)
        => LireValeurDePlanAc(plan, new Guid("54533251-82be-4824-96c1-47b60b740d00"), ModeDeBoostLu.Reglage);

    // La valeur secteur d'un reglage de plan, sous la forme que son op pose (un entier).
    private static Result<string?> LireValeurDePlanAc(Guid plan, Guid sousGroupe, Guid reglage)
    {
        uint valeur = 0;
        var rc = PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sousGroupe, ref reglage, ref valeur);
        return rc == 0
            ? Result<string?>.Ok(valeur.ToString(CultureInfo.InvariantCulture))
            : Illisible($"PowerReadACValueIndex rc={rc}");
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
        ref Guid powerSettingGuid, ref uint acValueIndex);

    private static Result<string?> LireService(string nom)
    {
        using var service = new ServiceController(nom);
        try
        {
            return Result<string?>.Ok(service.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending
                ? "running" : "stopped");
        }
        catch (InvalidOperationException)
        {
            return Result<string?>.Ok("notinstalled");
        }
    }

    private Result<string?> LireDrs(uint reglage)
    {
        // Absent du profil de base = valeur par defaut du pilote, qu'on ne connait pas : illisible, pas « 0 ».
        return _nvApi.Value.IsAvailable && _nvApi.Value.TryGetDrsGlobalSetting(reglage, out var valeur)
            ? Result<string?>.Ok(valeur.ToString(CultureInfo.InvariantCulture))
            : Illisible(Docteur.T("LC.DrsGlobal", $"0x{reglage:X8}"));
    }

    private static Result<string?> Illisible(string pourquoi) => Result<string?>.Fail(ErrorCodes.IoFailure, pourquoi);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
}
