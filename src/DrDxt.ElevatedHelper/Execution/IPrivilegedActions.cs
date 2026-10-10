using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Thin internal action seams for the real privileged executor.
//
// SECURITY / TESTABILITY: the op-DISPATCH logic (which typed op → which Windows
// action, GPU ops cleanly REFUSED) is the part worth unit-testing, so it is
// isolated behind these seams. A unit test injects fakes and asserts the right
// action ran with the right (already-allowlisted) arguments; the actual native
// call (Microsoft.Win32.Registry, powrprof, srclient, ServiceController) is the
// boundary that the live m2smoke proves — never exercised in unit tests.
//
// Every method returns a Result and must never throw: the executor wraps any
// failure as HELPER.OP_FAILED. The arguments these receive are already
// allowlist-validated by TypedPrivilegedServer; the real impls still touch only
// the exact leaf they are handed (defense in depth).
// -----------------------------------------------------------------------

/// <summary>Registry write/delete on the exact (hive, path, name) leaf.</summary>
public interface IRegistryActions
{
    Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value);
    Result DeleteValue(RegistryHiveKind hive, string path, string name);
}

/// <summary>Active power-plan switch (PowerSetActiveScheme).</summary>
public interface IPowerPlanActions
{
    Result SetActivePlan(Guid planGuid);

    /// <summary>07/10/2026 (L-CPU) : PERFBOOSTMODE sur secteur dans ce plan, puis le plan réactivé s'il est actif. Par
    /// défaut : refusé, pour les exécutants qui ne savent pas l'écrire.</summary>
    Result SetBoostModeAc(Guid planGuid, uint acValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "Boost mode write is not wired in this executor.");

    /// <summary>B3 (10/10/2026) : l'économie d'énergie de la carte sans fil sur secteur dans ce plan, puis le plan réactivé
    /// s'il est actif. Par défaut : refusé.</summary>
    Result SetWirelessPowerSavingAc(Guid planGuid, uint acValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "Wireless power saving write is not wired in this executor.");
}

/// <summary>System Restore point creation (SRSetRestorePoint / WMI) and enabling
/// System Restore for the system drive (Enable-ComputerRestore + shadow storage).</summary>
public interface IRestorePointActions
{
    Result CreatePoint(string description);

    /// <summary>Enables System Restore on the given (already allowlist-validated,
    /// system-only) drive and provisions a small shadow-storage allocation so a point
    /// can actually be created. Returns Result; must never throw.</summary>
    Result EnableSystemRestore(string driveLetter);
}

/// <summary>Windows service control (start / stop / query) and the bounded
/// PresentMon service install.</summary>
public interface IServiceActions
{
    Result Control(string serviceName, ServiceAction action);
    Result InstallPresentMonService();
}

/// <summary>NVIDIA driver-setting (DRS) write seam — the SAFE, public NVAPI surface
/// (no voltage / clock writes). Applies a single global driver setting via the DRS
/// session sequence. The settingId is already allowlist-validated (closed set). On
/// an AMD / no-NVIDIA box every call returns a clean Result.Fail — never throws.</summary>
public interface INvApiDrsActions
{
    Result SetGlobalSetting(uint settingId, uint value);

    /// <summary>
    /// L2 — ecrit un reglage dans le profil de CET EXECUTABLE. La hierarchie du driver est
    /// Application &gt; Global courant &gt; Base : une ecriture globale peut ne jamais atteindre un
    /// jeu qui possede son propre profil, et le produit annoncerait alors un reglage applique
    /// que le jeu ne verrait pas.
    ///
    /// Echoue proprement quand l'executable n'a pas de profil : on n'en cree pas, et on ne se
    /// rabat pas en silence sur le profil de base.
    /// </summary>
    Result SetSettingForApplication(string applicationPath, uint settingId, uint value);

    /// <summary>
    /// PHASE 18 F1 lot 3 — rend au profil de CET executable un reglage qu'il ne possedait pas.
    /// Succes uniquement si la relecture (session neuve) montre la provenance attendue : c'est la
    /// provenance, pas la valeur, qui prouve que le profil est redevenu ce qu'il etait.
    /// </summary>
    Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue);
}

// -----------------------------------------------------------------------
// INvApiTuningActions — the RISKY private NVAPI write seam (R2).
//
// This is the reverse-engineered, MSI-Afterburner-class surface (recipe §A/§B):
//   * SetCoreClockOffset — a P0/Graphics frequency delta (SetPstates20),
//   * SetPowerLimitPercent — the power target (ClientPowerPoliciesSetStatus).
// Both follow the GOLDEN GET-then-SET rule: read the current struct, mutate only the
// one field (P0 graphics clock / entry[0] power target), write it back. Never a
// zero-built write struct. The read methods (GetCoreClockOffsetMhz /
// GetPowerLimitPercent) expose the current value so a tweak can capture the real
// original before applying (faithful revert).
//
// Every method returns a Result and never throws across the IPC. On an AMD /
// no-NVIDIA box, or when the PRIVATE function id is not exported by the installed
// driver, the call fails clean (HELPER.OP_FAILED). The GpuIndex is the index into
// the enumerated physical GPU list (already allowlist-validated >= 0).
// -----------------------------------------------------------------------

/// <summary>RISKY private NVAPI tuning seam: P0 core-clock offset + power-limit
/// writes (GET-then-SET) plus the matching reads for capture-original. Fail-clean on
/// an AMD / no-NVIDIA box or a missing private function id — never throws.</summary>
public interface INvApiTuningActions
{
    /// <summary>Reads the current applied P0/Graphics core-clock offset (MHz) for a
    /// GPU. Unavailable / no NVIDIA → Fail(HELPER.OP_FAILED). Never throws.</summary>
    Result<int> GetCoreClockOffsetMhz(int gpuIndex);

    /// <summary>Reads the current applied power-limit (% of TDP) for a GPU.
    /// Unavailable / no NVIDIA → Fail(HELPER.OP_FAILED). Never throws.</summary>
    Result<int> GetPowerLimitPercent(int gpuIndex);

    /// <summary>GET-then-SET write of the P0/Graphics core-clock frequency delta
    /// (offsetMHz). Mutates only the P0 graphics clock entry; never builds the write
    /// struct from zero. Unavailable / no private id → Fail(HELPER.OP_FAILED).</summary>
    Result SetCoreClockOffset(int gpuIndex, int offsetMHz);

    /// <summary>GET-then-SET write of the power-limit (% of TDP), clamped to the
    /// driver's reported [min, max] PCM range. Mutates only entry[0]'s power target;
    /// never builds the write struct from zero. Unavailable → Fail(HELPER.OP_FAILED).</summary>
    Result SetPowerLimitPercent(int gpuIndex, float pct);
}

/// <summary>
/// M3 lot 2 — the firmware-boot seam: ask Windows to send the next boot into the firmware
/// setup UI, and withdraw that request.
///
/// Both go through <c>shutdown.exe</c> (<c>/r /fw</c> and <c>/a /fw</c>), the documented
/// facility that owns the <c>OsIndications</c> bit. DrDXT writes no firmware variable itself,
/// so there is no NVRAM boundary to get wrong and nothing an antivirus can read as a firmware
/// write. The seam exists so the native call is faked in tests, like every other action here.
/// </summary>
public interface IFirmwareBootActions
{
    /// <summary>Request "next boot goes to firmware setup" and restart after the given delay.</summary>
    Result RequestFirmwareSetupReboot(int delaySeconds);

    /// <summary>Withdraw a pending firmware boot. Safe to call when nothing is pending.</summary>
    Result CancelPendingFirmwareBoot();

    /// <summary>Read ONE EFI firmware variable. Read-only by construction: there is deliberately
    /// no write counterpart on this interface. Failing to read is not proof the firmware refused
    /// — win32 error 1314 means the elevated privilege is absent, which is inconclusive.</summary>
    Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid);
}

/// <summary>
/// 24/09/2026 — le plafond d'envoi QoS (politiques Windows en memoire, ActiveStore). La couture
/// existe pour que l'appel natif soit simule dans les tests, comme toutes les actions ici.
/// </summary>
public interface INetworkQosActions
{
    /// <summary>Pose (ou remplace) les deux politiques, puis les relit : echec si elles ne sont
    /// pas la avec les bonnes valeurs.</summary>
    Result ApplyUploadCap(string gameExeName, long capBitsPerSecond);

    /// <summary>Retire les deux politiques et verifie qu'il n'en reste aucune. Idempotent.</summary>
    Result RemoveUploadCap();
}

/// <summary>24/09/2026 — ecrire un element BCD de l'entree courante, et le relire.</summary>
public interface IBcdActions
{
    Result DeleteValue(string element);
    Result SetValue(string element, string value);
}

/// <summary>B1 (10/10/2026) : la limite de puissance du processeur Intel (MSR 0x610) par PawnIO. Par défaut : refusé,
/// pour les exécutants qui ne savent pas l'écrire (le service de démarrage, le banc).</summary>
public interface ICpuPowerActions
{
    Result SetPackagePowerLimit(ulong valeur, ulong origine) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit write is not wired in this executor.");
}

/// <summary>GPU-OC (08/10/2026) : le ventilateur d'une carte en mode Expert. Poser ne fait que monter, et se relit ;
/// rendre se prouve par la politique relue (voir <see cref="NvmlVentilateurActions"/>).</summary>
public interface IVentilateurActions
{
    Result Poser(int gpuIndex, int pourcent);
    Result Rendre(int gpuIndex);
}
