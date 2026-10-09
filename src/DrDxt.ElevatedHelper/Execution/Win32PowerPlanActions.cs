using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32PowerPlanActions — the real active power-plan switch (m2smoke boundary).
//
// PowerSetActiveScheme(powrprof.dll) with the requested GUID. The GUID was
// already validated by the allowlist against the known Windows plan set. Any
// non-zero Win32 result → HELPER.OP_FAILED; this never throws.
//
// 07/10/2026 (revue F-28) : activer un plan peut remettre le « mode d'alimentation » de Windows 11 (Meilleure
// autonomie, Équilibré, Meilleures performances ; autrefois « overlay ») à son défaut. Microsoft Learn documente sa lecture
// et son écriture : PowerGetUserConfiguredACPowerMode / PowerSetUserConfiguredACPowerMode (powrprof.dll, Windows 11).
// Le mode sur secteur est lu avant chaque activation ; s'il a bougé, il est reposé, puis relu. Avant Windows 11 (fonction
// absente), rien n'est lu ni reposé.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32PowerPlanActions : IPowerPlanActions
{
    /// <summary>Ce que l'exécutant demande à powrprof : la couture des tests, aucun test ne touche la machine.</summary>
    internal interface IAlimentation
    {
        Guid? PlanActif();
        uint EcrireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage, uint valeur);
        /// <summary>La valeur secteur d'un réglage du plan ; <c>null</c> = non lue.</summary>
        uint? LireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage);
        uint Activer(Guid plan);
        /// <summary>Le mode d'alimentation choisi sur secteur ; <c>null</c> = non lu (avant Windows 11).</summary>
        Guid? ModeSecteur();
        uint PoserModeSecteur(Guid mode);
    }

    private readonly IAlimentation _alimentation;

    public Win32PowerPlanActions() : this(new AlimentationWindows()) { }

    internal Win32PowerPlanActions(IAlimentation alimentation) => _alimentation = alimentation;

    public Result SetActivePlan(Guid planGuid)
    {
        try
        {
            // Revue F-36 : plan activé mais mode d'alimentation pas reposé = l'op est dite ratée ; le plan d'avant
            // revient, et le mode d'avant est retenté (revue du 07/10 : seul le plan revenait).
            var avant = _alimentation.PlanActif();
            var mode = _alimentation.ModeSecteur();
            var r = ActiverEnGardantLeMode(planGuid);
            if (r.IsOk || avant is not { } plan || plan == planGuid) return r;
            _alimentation.Activer(plan);
            return Fail($"{r.Error?.Message} {RendreLeMode(mode)}");
        }
        catch (Exception ex)
        {
            return Fail($"Power-plan switch failed: {ex.GetType().Name}.");
        }
    }

    // 07/10/2026 (L-CPU) : la valeur secteur de PERFBOOSTMODE dans ce plan (valeur deja bornee par l'allowlist).
    // Microsoft Learn (PowerWriteACValueIndex) : la valeur du plan actif ne prend effet qu'apres PowerSetActiveScheme ;
    // un autre plan n'est jamais active ici.
    public Result SetBoostModeAc(Guid planGuid, uint acValue)
    {
        try
        {
            // Le plan actif est lu avant d'ecrire : non lu, on ne saurait pas s'il faut le reactiver, et une valeur ecrite
            // sans effet passerait pour posee.
            if (_alimentation.PlanActif() is not { } actif) return Fail("Active power plan unread: boost mode not written.");
            // Revue F-36 : la valeur d'avant, relue, pour la rendre si la suite échoue (une op « ratée » ne laisse rien).
            if (_alimentation.LireValeurSecteur(planGuid, SousGroupeProcesseur, ModeDeBoostLu.Reglage) is not { } avant)
                return Fail("Boost mode unread: not written.");
            var mode = _alimentation.ModeSecteur();
            var rc = _alimentation.EcrireValeurSecteur(planGuid, SousGroupeProcesseur, ModeDeBoostLu.Reglage, acValue);
            if (rc != 0) return Fail($"PowerWriteACValueIndex failed (Win32 {rc}).");
            if (actif != planGuid) return Result.Ok();
            var r = ActiverEnGardantLeMode(planGuid);
            if (r.IsOk) return r;
            _alimentation.EcrireValeurSecteur(planGuid, SousGroupeProcesseur, ModeDeBoostLu.Reglage, avant);
            _alimentation.Activer(planGuid);
            return Fail($"{r.Error?.Message} Boost mode set back to {avant}. {RendreLeMode(mode)}");
        }
        catch (Exception ex)
        {
            return Fail($"Boost mode write failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>Après un retour en arrière : le mode d'alimentation d'avant, retenté une fois et relu ; la phrase dit ce qui
    /// est vrai, pour qu'un échec ne se lise pas « rien n'a changé ».</summary>
    private string RendreLeMode(Guid? mode)
    {
        if (mode is not { } m || _alimentation.ModeSecteur() == m) return "Power mode unchanged.";
        return _alimentation.PoserModeSecteur(m) == 0 && _alimentation.ModeSecteur() == m
            ? "Power mode set back."
            : $"Power mode NOT set back to {m}: set it again in Windows Settings, System, Power.";
    }

    private Result ActiverEnGardantLeMode(Guid plan)
    {
        var avant = _alimentation.ModeSecteur();
        var rc = _alimentation.Activer(plan);
        if (rc != 0) return Fail($"PowerSetActiveScheme failed (Win32 {rc}).");
        if (avant is not { } mode || _alimentation.ModeSecteur() == mode) return Result.Ok();
        rc = _alimentation.PoserModeSecteur(mode);
        return rc == 0 && _alimentation.ModeSecteur() == mode
            ? Result.Ok()
            : Fail($"Plan activated, but the power mode it reset could not be restored to {mode} (Win32 {rc}).");
    }

    private static readonly Guid SousGroupeProcesseur = new("54533251-82be-4824-96c1-47b60b740d00");

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);

    /// <summary>Le vrai powrprof.</summary>
    private sealed class AlimentationWindows : IAlimentation
    {
        public Guid? PlanActif() => DrDxt.Core.PlanActif.Lire().Plan;

        public uint EcrireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage, uint valeur)
            => PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sousGroupe, ref reglage, valeur);

        public uint? LireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage)
        {
            uint valeur = 0;
            return PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sousGroupe, ref reglage, ref valeur) == 0 ? valeur : null;
        }

        [DllImport("powrprof.dll", SetLastError = false)]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, ref uint acValueIndex);

        public uint Activer(Guid plan) => PowerSetActiveScheme(IntPtr.Zero, ref plan);

        public Guid? ModeSecteur()
        {
            try { return PowerGetUserConfiguredACPowerMode(out var mode) == 0 ? mode : null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public uint PoserModeSecteur(Guid mode) => PowerSetUserConfiguredACPowerMode(ref mode);

        [DllImport("powrprof.dll", SetLastError = false)]
        private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
            ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

        // ERROR_SUCCESS (0) on success; a Win32 error code otherwise.
        [DllImport("powrprof.dll", SetLastError = false)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll", SetLastError = false)]
        private static extern uint PowerGetUserConfiguredACPowerMode(out Guid powerModeGuid);

        [DllImport("powrprof.dll", SetLastError = false)]
        private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid powerModeGuid);
    }
}
