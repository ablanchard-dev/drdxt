using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;
using DrDxt.Reconciler.Desinstallation;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>11/09/2026 — la lecture de la valeur courante, pour la desinstallation : registre, plan actif,
/// etat d'un service, reglage DRS du profil de base. Lecture seule ; tout echec rend <c>Fail</c> (illisible
/// ⇒ rien n'est touche), jamais une exception.
/// Simplification voulue : double volontairement les lecteurs bruts de DrDxt.Reconciler.Adapters, qui tirent Detection
/// (et ses dependances) — le helper eleve reste sans Detection.</summary>
[SupportedOSPlatform("windows")]
public sealed class LectureDesCiblesWin32 : ILectureDesCibles
{
    private readonly Lazy<INvApiNative> _nvApi;
    private readonly Lazy<DrDxt.Detection.Gpu.Adlx.IAdlxNative> _adlx;

    /// <param name="nvApi">Absent : le vrai NVAPI, cree a la premiere lecture DRS.</param>
    /// <param name="adlx">08/10/2026 : le pilote AMD, le MEME que celui qui écrit (un seul ADLXInitialize par processus).</param>
    public LectureDesCiblesWin32(INvApiNative? nvApi = null, DrDxt.Detection.Gpu.Adlx.IAdlxNative? adlx = null)
    {
        _nvApi = new(() => nvApi ?? new NvApiNative());
        _adlx = new(() => adlx ?? new DrDxt.Detection.Gpu.Adlx.AdlxNative());
    }

    public Result<string?> Lire(PrivilegedOp ecriture)
    {
        try
        {
            return ecriture switch
            {
                SetRegistryValueOp s => LireRegistre(s),
                SetActivePowerPlanOp => LirePlanActif(),
                SetProcessorBoostModeOp b => LireModeDeBoost(b.PlanGuid),
                RunServiceControlOp r => LireService(r.ServiceName),
                ApplyNvApiDrsSettingOp d when OpAllowlist.IsBaseProfileScope(d.ProfileName) => LireDrs(d.SettingId),
                ApplyNvApiDrsSettingOp d when OpAllowlist.IsQualifiedExecutablePath(d.ProfileName)
                    => LireDrsAuJeu(d.ProfileName, d.SettingId),
                // 08/10/2026 : les réglages de la carte, relus sous la forme que leur op pose (un entier).
                ApplyNvApiPowerLimitOp n => _nvApi.Value.IsAvailable && _nvApi.Value.TryReadPowerLimitPct(n.GpuIndex, out var pct)
                    ? Result<string?>.Ok(pct.ToString(CultureInfo.InvariantCulture))
                    : Illisible("limite de puissance NVIDIA non lue"),
                ApplyAdlxTuningOp a => LireAdlx(a),
                _ => Illisible($"aucune lecture pour {ecriture.GetType().Name}"),
            };
        }
        catch (Exception ex)
        {
            return Illisible(ex.Message);
        }
    }

    private Result<string?> LireAdlx(ApplyAdlxTuningOp a)
    {
        if (!_adlx.Value.IsAvailable) return Illisible("pilote AMD (ADLX) absent");
        int? lu = a.Kind switch
        {
            AdlxTuningKind.PowerLimit => _adlx.Value.TryReadPower(a.GpuIndex, out var p) ? p.PowerLimitPct : null,
            AdlxTuningKind.AntiLag => _adlx.Value.LireReglages().AntiLag is { } on ? (on ? 1 : 0) : null,
            AdlxTuningKind.FrameRateTarget => _adlx.Value.LireReglages().FrtcCode,
            AdlxTuningKind.VerticalRefresh => _adlx.Value.LireReglages().VsyncMode,
            _ => null,
        };
        return lu is { } v ? Result<string?>.Ok(v.ToString(CultureInfo.InvariantCulture)) : Illisible($"réglage AMD {a.Kind} non lu");
    }

    // NVDRS_SETTING_LOCATION : la valeur appartient au profil lu (DrsSettingOrigin, PG-12072-001).
    private const uint EmplacementProfilCourant = 0;

    /// <summary>14/09/2026 — un reglage pose au profil d'un JEU (PreferMaxPerf, RenderQueueCap, VrrVsyncCap).
    /// Sans ce cas il etait « illisible » et la desinstallation ne le rendait jamais.
    /// Un reglage que le jeu ne possede plus se relit a la valeur HERITEE du global, qui peut valoir
    /// exactement ce que DrDXT avait pose : il est lu absent, sinon on rendrait un reglage disparu.</summary>
    private Result<string?> LireDrsAuJeu(string jeu, uint reglage)
    {
        if (!_nvApi.Value.IsAvailable || !_nvApi.Value.TryReadDrsSettingForApplication(jeu, reglage, out var lu))
            return Illisible($"réglage DRS 0x{reglage:X8} du jeu non lu (NVAPI absent ou profil introuvable)");
        return Result<string?>.Ok(lu.SettingLocation == EmplacementProfilCourant
            ? lu.Value.ToString(CultureInfo.InvariantCulture)
            : null);
    }

    /// <summary>11/09/2026 — la cible existe-t-elle encore ? On interroge le CONTENANT, pas la valeur :
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
                // Le plan d'alimentation VISE par le retour : s'il a ete efface, l'activer echouerait
                // (ou, pire, on ecrirait un GUID qui ne designe plus rien).
                SetActivePowerPlanOp p => PlanExiste(p.PlanGuid),
                SetProcessorBoostModeOp b => PlanExiste(b.PlanGuid),
                RunServiceControlOp r => ServiceExiste(r.ServiceName),
                // NVAPI : le pilote doit repondre. Absent ⇒ la cible n'existe plus sur cette machine.
                ApplyNvApiDrsSettingOp or DeleteNvApiDrsSettingOp => Result<bool>.Ok(_nvApi.Value.IsAvailable),
                // 08/10/2026 : la carte doit répondre ; pilote absent (carte changée) = la cible n'existe plus.
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
        if (racine is null) return Result<bool>.Fail(ErrorCodes.IoFailure, $"ruche {ruche} non lue");
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
        if (racine is null) return Illisible($"ruche {s.Hive} non lue");

        using var cle = racine.OpenSubKey(s.Path, writable: false);
        var brute = cle?.GetValue(s.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (brute is null) return Result<string?>.Ok(null);

        return (s.Kind, brute) switch
        {
            (RegistryValueType.DWord, int i) => Result<string?>.Ok(unchecked((uint)i).ToString(CultureInfo.InvariantCulture)),
            (RegistryValueType.QWord, long l) => Result<string?>.Ok(unchecked((ulong)l).ToString(CultureInfo.InvariantCulture)),
            (RegistryValueType.String, string texte) => Result<string?>.Ok(texte),
            _ => Illisible($"la valeur {s.Name} n'est pas un {s.Kind}"),
        };
    }

    private static Result<string?> LirePlanActif()
    {
        var (plan, rc, exception) = PlanActif.Lire();
        return plan is { } guid
            ? Result<string?>.Ok(guid.ToString("D", CultureInfo.InvariantCulture))
            : Illisible(exception ?? $"PowerGetActiveScheme rc={rc}");
    }

    /// <summary>07/10/2026 (L-CPU) : PERFBOOSTMODE sur secteur dans ce plan (PowerReadACValueIndex, lecture seule).</summary>
    private static Result<string?> LireModeDeBoost(Guid plan)
    {
        var sousGroupe = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var reglage = ModeDeBoostLu.Reglage;
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
            : Illisible($"réglage DRS 0x{reglage:X8} non lu (NVAPI absent ou réglage au défaut du pilote)");
    }

    private static Result<string?> Illisible(string pourquoi) => Result<string?>.Fail(ErrorCodes.IoFailure, pourquoi);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
}
