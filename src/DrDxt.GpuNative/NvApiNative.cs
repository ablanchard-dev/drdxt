using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiNative — the SINGLE real P/Invoke boundary into nvapi64.dll.
//
// ARCHITECTURE
// ────────────
// nvapi64.dll exports only nvapi_QueryInterface(id) → fn pointer; everything else is
// reached by QueryInterface(id) → GetDelegateForFunctionPointer. NvApiEntry owns that
// entry mechanism (+ NvAPI_Initialize, EnumPhysicalGPUs, the MakeVersion golden rule);
// NvApiNative composes it and carries the reverse-engineered function IDs and the
// GET-then-SET write recipe. This is the NVIDIA mirror of AdlxNative: ONE boundary with
// Both the reads (non-elevated) AND the writes (elevated path), so the function IDs and
// the write recipe — expensive to validate live on a real RTX 4060 — have exactly ONE
// copy, never a divergent duplicate.
//
// SURFACES
// ────────
//   READS  : TryReadCoreOffsetMhz (GetPstates20)        — non-elevated.
//            TryReadPowerLimitPct (ClientPowerPoliciesGetStatus).
//   WRITES : TrySetCoreClockOffset (Get→SetPstates20, minimal write, editable-gated).
//            TrySetPowerLimitPercent (GetInfo→clamp→GetStatus→SetStatus).
//            TrySetDrsGlobalSetting (the SAFE public DRS session sequence).
//
// Golden safety rules (recipe):
//   #1 version word = Marshal.SizeOf(struct) | (1 << 16) at runtime (NvApiEntry.MakeVersion)
//      — a size mismatch becomes the driver's SAFE IncompatibleStructVersion, not a write.
//   #2 GET-then-SET: read the FULL current struct, mutate only the one field IN PLACE,
//      write it back — never a zero-built write struct (which would send garbage).
//   #3 status 0 = Ok; any non-zero → false, never proceed.
//
// Fail-clean contract (identical to AdlxNative): on an AMD / no-NVIDIA box nvapi64.dll
// is absent → IsAvailable is false and every Try* returns false; a missing PRIVATE
// function id, a struct-size mismatch, or any interop fault all return false. Never
// throws to the caller — a catch-all guards every code path.
//
// The conservative value-bounds POLICY (offset / power bands, mirrored from GpuSafeBounds)
// is not enforced here — it lives in the helper's NvApiTuningMath and is checked by the
// Write orchestrators (NvApiTuningActions) before they reach this boundary, exactly like
// AdlxTuningMath sits in front of AdlxNative. This boundary is recipe + driver-clamp only.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvApiNative : INvApiNative
{
    // --- tuning function IDs (recipe). GET = documented; writes = PRIVATE (Afterburner-class). ---
    private const uint Id_GetPstates20 = 0x6FF81213;                 // documented
    private const uint Id_SetPstates20 = 0x0F4DAE6B;                 // PRIVATE
    private const uint Id_ClientPowerPoliciesGetInfo = 0x34206D86;   // PRIVATE
    private const uint Id_ClientPowerPoliciesGetStatus = 0x70916171; // PRIVATE
    private const uint Id_ClientPowerPoliciesSetStatus = 0xAD95F5ED; // PRIVATE

    // --- DRS function IDs (recipe table, all in NVIDIA's public header). ---
    private const uint Id_DRS_CreateSession = 0x0694D52E;
    private const uint Id_DRS_LoadSettings = 0x375DBD6B;
    private const uint Id_DRS_GetBaseProfile = 0xDA8466A0;
    // Offset verifie contre NvAPIWrapper/FunctionId.cs et nvidiaProfileInspector.
    private const uint Id_DRS_FindApplicationByName = 0xEEE566B2;
    private const uint Id_DRS_GetSetting = 0x73BF8338;    // NvAPI_DRS_GetSetting — verified vs NvAPIWrapper FunctionId.cs + jNizM NvAPI_IDs.txt
    private const uint Id_DRS_SetSetting = 0x577DD202;
    private const uint Id_DRS_SaveSettings = 0xFCBC7E14;
    private const uint Id_DRS_DestroySession = 0xDAD9CFF8;

    // L8 suite (VRR, lecture) — verifies le 10/09/2026 contre jNizM NvAPI_IDs.txt et le fichier
    // OFFICIEL NVIDIA nvapi_interface.h : { "NvAPI_DISP_GetDisplayIdByDisplayName", 0xae457190 },
    // { "NvAPI_Disp_GetVRRInfo", 0xdf8fda57 }. (NvAPIWrapper n'a pas GetVRRInfo.)
    private const uint Id_DISP_GetDisplayIdByDisplayName = 0xAE457190;
    private const uint Id_Disp_GetVRRInfo = 0xDF8FDA57;

    private const uint GraphicsClockDomain = 0;  // PublicClockDomain GRAPHICS
    // PublicClockDomain MEMORY. NV_GPU_PUBLIC_CLOCK_ID (NvAPIWrapper PublicClockId): the
    // documented sibling of GRAPHICS=0 in the same enum — GRAPHICS=0, MEMORY=4, PROCESSOR=7,
    // VIDEO=8. Pinned by the NvApiFinderTests memory freeze-test so any drift is caught.
    private const uint MemoryClockDomain = 4;
    private const uint PstateP0 = 0;             // PerformanceStateId P0

    // NVDRS_SETTING string + value buffer sizes (NvApiDriverSettings.h).
    private const int NvapiUnicodeStringMax = 2048;   // settingName: wchar[2048].
    private const int NvapiSettingMaxValues = 4100;   // predefined/current value blobs.
    private const uint NvdrsSettingV1Version = 1;
    private const uint NvdrsApplicationV4Version = 4;
    private const uint NvdrsSettingDwordType = 0;     // NVDRS_DWORD_TYPE.

    private readonly NvApiEntry _entry;

    public NvApiNative() : this(new NvApiEntry()) { }

    internal NvApiNative(NvApiEntry entry) => _entry = entry;

    public bool IsAvailable => _entry.IsAvailable;

    private IntPtr ResolveHandle(int gpuIndex)
    {
        var gpus = _entry.EnumPhysicalGpus();
        return gpuIndex < gpus.Count ? gpus[gpuIndex] : IntPtr.Zero;
    }

    // ── READS ─────────────────────────────────────────────────────────────

    public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            if (get is null) return false;

            var info = NewPstates20Info();
            if (get(handle, ref info) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0GraphicsClockIndex(info, out var p, out var c)) return false;

            offsetMhz = NvApiConvert.KhzToMhz(info.Pstates[p].Clocks[c].FreqDeltaKhz.Value);
            return true;
        }
        catch { return false; }
    }

    public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        // Same documented GET as the core read (a SAFE read, not a private write), locating
        // the P0/MEMORY clock-domain delta instead of GRAPHICS. The undervolt apply reads
        // Both offsets to capture a faithful revert, so this is real — a fail-clean placebo
        // here would make NVIDIA undervolt never apply.
        offsetMhz = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            if (get is null) return false;

            var info = NewPstates20Info();
            if (get(handle, ref info) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0MemoryClockIndex(info, out var p, out var c)) return false;

            offsetMhz = NvApiConvert.KhzToMhz(info.Pstates[p].Clocks[c].FreqDeltaKhz.Value);
            return true;
        }
        catch { return false; }
    }

    public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
    {
        powerLimitPct = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var getStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesGetStatus);
            if (getStatus is null) return false;

            var status = NewPowerStatus();
            if (getStatus(handle, ref status) != (int)NvApiStatus.Ok) return false;
            if (status.Count == 0 || status.Entries is null || status.Entries.Length == 0) return false;

            // Note: % is whole-number here; a fractional stock target (e.g. 87.5% = 87_500 PCM)
            // captures as 88 → revert is ±0.5% (negligible). The core-offset read is exact.
            powerLimitPct = NvApiConvert.PcmToPercent(status.Entries[0].PowerTargetPcm);
            return true;
        }
        catch { return false; }
    }

    /// <summary>`NV_GET_VRR_INFO_V1` : version, mot de bits, reservedEx[4] = 24 octets. La version se
    /// calcule sur la taille reelle (regle d'or #1) : une divergence donne l'erreur SURE du pilote.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvGetVrrInfoV1
    {
        public uint Version;
        public uint Bits;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] ReservedEx;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDisplayIdByDisplayNameDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string displayName, out uint displayId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVrrInfoDelegate(uint displayId, ref NvGetVrrInfoV1 info);

    public bool TryReadVrr(string displayName, out VrrReading reading)
    {
        reading = default;
        if (!IsAvailable || string.IsNullOrWhiteSpace(displayName)) return false;
        try
        {
            var parNom = _entry.GetDelegate<GetDisplayIdByDisplayNameDelegate>(Id_DISP_GetDisplayIdByDisplayName);
            var lireVrr = _entry.GetDelegate<GetVrrInfoDelegate>(Id_Disp_GetVRRInfo);
            if (parNom is null || lireVrr is null) return false;

            // « The display must be active to retrieve the displayId » (doc NVIDIA) : un ecran
            // pilote par une autre carte ou inactif rend une erreur, donc false — non lu.
            if (parNom(displayName, out var displayId) != (int)NvApiStatus.Ok) return false;

            var info = new NvGetVrrInfoV1
            {
                Version = NvApiEntry.MakeVersion<NvGetVrrInfoV1>(1),
                ReservedEx = new uint[4],
            };
            if (lireVrr(displayId, ref info) != (int)NvApiStatus.Ok) return false;

            reading = VrrReading.Decoder(info.Bits);
            return true;
        }
        catch { return false; }
    }

    // ── WRITES ────────────────────────────────────────────────────────────

    public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz)
    {
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            var set = _entry.GetDelegate<SetPstates20Delegate>(Id_SetPstates20);
            if (get is null || set is null) return false;

            // GET-then-SET (golden rule #2): read the FULL current struct, mutate only the
            // P0/Graphics frequency delta IN PLACE, write the same struct back. We never
            // zero-build a write (that would send garbage ClockType/flags/ranges). A struct-
            // size mismatch is rejected here (SAFE IncompatibleStructVersion) before any write.
            var current = NewPstates20Info();
            if (get(handle, ref current) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0GraphicsClockIndex(current, out var p, out var c)) return false;

            // The clock-entry editable bit is the authoritative per-clock gate (NvAPIWrapper
            // IPerformanceStates20ClockEntry.IsEditable = _Flags bit0). The info-/pstate-level
            // editable flags are intentionally not gated here: gating them risks a false-
            // negative that refuses a valid write, and the leaf bit is sufficient.
            if ((current.Pstates[p].Clocks[c].Flags & 1u) == 0u) return false;

            // Minimal write — the SetPstates20 convention (what MSI Afterburner sends): submit
            // Only the single P0/Graphics clock delta, not the full GET'd struct. Echoing the
            // whole struct back (with its base-voltage entries) is REJECTED by the driver with
            // NotSupported (-104) — voltage edits are restricted (proven on a live RTX 4060).
            // We copy the GET'd entry's real ClockType + delta-range so no field is garbage,
            // change only the frequency delta value, and touch no voltages (NumBaseVoltages = 0).
            var write = NewPstates20Info();
            write.NumPstates = 1;
            write.NumClocks = 1;
            write.NumBaseVoltages = 0;
            write.Pstates[0].StateId = PstateP0;
            write.Pstates[0].Clocks[0].DomainId = GraphicsClockDomain;
            write.Pstates[0].Clocks[0].ClockType = current.Pstates[p].Clocks[c].ClockType;
            write.Pstates[0].Clocks[0].FreqDeltaKhz = current.Pstates[p].Clocks[c].FreqDeltaKhz;
            write.Pstates[0].Clocks[0].FreqDeltaKhz.Value = NvApiConvert.MhzToKhz(offsetMhz);

            return set(handle, ref write) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz)
    {
        // Exact mirror of TrySetCoreClockOffset for the P0/MEMORY domain (4): same GET-then-SET
        // recipe, same minimal single-entry write convention (what Afterburner sends for a memory
        // offset), same editable-bit gate. The memory read over this struct is already live-proven
        // (the undervolt capture uses it); only the domain in the write differs from the
        // live-validated core path.
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            var set = _entry.GetDelegate<SetPstates20Delegate>(Id_SetPstates20);
            if (get is null || set is null) return false;

            var current = NewPstates20Info();
            if (get(handle, ref current) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0MemoryClockIndex(current, out var p, out var c)) return false;

            // The clock-entry editable bit is the authoritative per-clock gate (same as core).
            if ((current.Pstates[p].Clocks[c].Flags & 1u) == 0u) return false;

            var write = NewPstates20Info();
            write.NumPstates = 1;
            write.NumClocks = 1;
            write.NumBaseVoltages = 0;
            write.Pstates[0].StateId = PstateP0;
            write.Pstates[0].Clocks[0].DomainId = MemoryClockDomain;
            write.Pstates[0].Clocks[0].ClockType = current.Pstates[p].Clocks[c].ClockType;
            write.Pstates[0].Clocks[0].FreqDeltaKhz = current.Pstates[p].Clocks[c].FreqDeltaKhz;
            write.Pstates[0].Clocks[0].FreqDeltaKhz.Value = NvApiConvert.MhzToKhz(offsetMhz);

            return set(handle, ref write) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    public bool TrySetPowerLimitPercent(int gpuIndex, float pct)
    {
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var getInfo = _entry.GetDelegate<PowerInfoDelegate>(Id_ClientPowerPoliciesGetInfo);
            var getStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesGetStatus);
            var setStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesSetStatus);
            if (getInfo is null || getStatus is null || setStatus is null) return false;

            // GET info → the driver's valid [min, max] PCM bounds for this GPU.
            var info = NewPowerInfo();
            if (getInfo(handle, ref info) != (int)NvApiStatus.Ok) return false;
            if (info.Count == 0 || info.Entries is null || info.Entries.Length == 0) return false;

            var min = info.Entries[0].MinPowerPcm;
            var max = info.Entries[0].MaxPowerPcm;
            if (max < min || max == 0) return false;

            var clamped = NvApiConvert.ClampPcm(NvApiConvert.PercentToPcm(pct), min, max);

            // GET status → mutate only entry[0]'s power target IN PLACE → SET (golden rule #2).
            var status = NewPowerStatus();
            if (getStatus(handle, ref status) != (int)NvApiStatus.Ok) return false;
            if (status.Count == 0 || status.Entries is null || status.Entries.Length == 0) return false;

            status.Entries[0].PowerTargetPcm = clamped;
            return setStatus(handle, ref status) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    // ── DRS (SAFE public driver-setting write) ──────────────────────────────

    public bool TrySetDrsGlobalSetting(uint settingId, uint value)
    {
        if (!IsAvailable) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var getBaseProfile = _entry.GetDelegate<GetBaseProfileDelegate>(Id_DRS_GetBaseProfile);
            var setSetting = _entry.GetDelegate<SetSettingDelegate>(Id_DRS_SetSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || getBaseProfile is null
                || setSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                if (getBaseProfile(session, out var profile) != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return false;

                // Build the setting struct. Pre-allocate every ByValArray (golden rule #2)
                // and compute the version word at runtime (golden rule #1).
                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };
                // The DWORD lives in the first 4 bytes of currentValue (recipe §C).
                BitConverter.TryWriteBytes(setting.currentValue, value);

                if (setSetting(session, profile, ref setting) != (int)NvApiStatus.Ok) return false;
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                // Always tear the session down, even on a mid-sequence failure.
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (ecriture par APPLICATION — L2) ─────────────────────────────────
    //
    // Meme sequence que l'ecriture globale, a un appel pres : FindApplicationByName remplace
    // GetBaseProfile. Cet appel-la change tout -- la hierarchie du driver est
    // Application > Global courant > Base (PG-12072-001 p.2), donc une ecriture globale peut ne
    // Jamais atteindre un jeu qui possede son propre profil, et le produit annoncerait un
    // reglage applique que le jeu ne verrait pas.
    public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var setSetting = _entry.GetDelegate<SetSettingDelegate>(Id_DRS_SetSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || setSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                // Pre-allouer chaque ByValTStr + mot de version calcule au runtime (regles d'or #1 et #2).
                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // NVAPI_EXECUTABLE_NOT_FOUND quand l'exe n'a pas de profil a lui. On rend false
                // et on n'en cree pas : fabriquer un profil ecrirait une entree permanente dans
                // la base du driver pour un jeu peut-etre jamais lance, et obligerait le revert a
                // savoir SUPPRIMER un profil au lieu de restaurer une valeur. Il n'y a pas non
                // plus de repli silencieux sur le profil de base -- ce serait ecrire ailleurs que
                // la ou l'appelant a demande.
                if (findApp(session, applicationPath, out var profile, ref app) != (int)NvApiStatus.Ok
                    || profile == IntPtr.Zero)
                {
                    return false;
                }

                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };
                // Le DWORD vit dans les 4 premiers octets de currentValue (recette §C).
                BitConverter.TryWriteBytes(setting.currentValue, value);

                if (setSetting(session, profile, ref setting) != (int)NvApiStatus.Ok) return false;
                // NVIDIA documente que SaveSettings ecrase ce qu'un autre processus a modifie
                // entre-temps, sans notification (PG-12072-001 p.11). D'ou une session qui ne
                // contient rien d'autre que cette ecriture.
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (suppression par APPLICATION — PHASE 18 F1 lot 3, 10/09/2026) ────
    //
    // L'INVERSE d'une ecriture au profil d'un jeu quand ce profil ne possedait pas le reglage.
    // `NvAPI_DRS_DeleteProfileSetting(hSession, hProfile, settingId)` : « deletes a setting or sets
    // it back to predefined value » (docs.nvidia.com/nvapi, groupe drsapi). Identifiant verifie le
    // 10/09/2026 dans NvAPIWrapper `FunctionId.cs` Et jNizM `NvAPI_IDs.txt` (temoins GetSetting
    // 0x73BF8338 / SetSetting 0x577DD202, egaux aux constantes ci-dessus).
    // Jamais NvAPI_DRS_RestoreProfileDefault : il touche le profil entier et peut le supprimer.
    private const uint Id_DRS_DeleteProfileSetting = 0xE4A26362;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeleteProfileSettingDelegate(IntPtr session, IntPtr profile, uint settingId);

    public bool TryDeleteDrsSettingForApplication(string applicationPath, uint settingId)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var deleteSetting = _entry.GetDelegate<DeleteProfileSettingDelegate>(Id_DRS_DeleteProfileSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || deleteSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // Pas de profil a lui : rien a supprimer, et surtout pas de repli sur la base.
                if (findApp(session, applicationPath, out var profile, ref app) != (int)NvApiStatus.Ok
                    || profile == IntPtr.Zero)
                {
                    return false;
                }

                if (deleteSetting(session, profile, settingId) != (int)NvApiStatus.Ok) return false;
                // Session qui ne contient rien d'autre que cette suppression (PG-12072-001 p.11 :
                // SaveSettings ecrase ce qu'un autre processus a modifie entre-temps).
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (SAFE public driver-setting read; NON-elevated) ─────────────────
    public bool TryGetDrsGlobalSetting(uint settingId, out uint value)
    {
        // Delegates so the two overloads cannot drift: one session sequence, one recipe.
        var ok = TryReadDrsGlobalSetting(settingId, out var reading);
        value = ok ? reading.Value : 0;
        return ok;
    }

    public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        => LireDrsAuJeu(applicationPath, settingId, out reading) == INvApiNative.LectureAuJeu.Lu;

    public INvApiNative.LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading)
    {
        const INvApiNative.LectureAuJeu Illisible = INvApiNative.LectureAuJeu.Illisible;
        reading = default;
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return Illisible;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var getSetting = _entry.GetDelegate<GetSettingDelegate>(Id_DRS_GetSetting);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || getSetting is null || destroySession is null)
            {
                return Illisible;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return Illisible;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return Illisible;

                // Pre-allocate every ByValTStr + runtime version word (golden rules #1 and #2).
                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // NVAPI_EXECUTABLE_NOT_FOUND when the exe has no profile at all, and
                // NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS for a bare file name. Both are honest
                // "no answer" -> false.
                // CORRECTED 10/09/2026 (measured on cod.exe): this comment used to say "there is
                // NO automatic fallback to the base profile". That was false. Once the profile
                // exists, GetSetting DOES fall back: a setting the profile does not own comes back
                // with the value the driver applies and settingLocation = GLOBAL_PROFILE. The
                // location is kept in the reading so callers can tell "the game owns it" from
                // "the game inherits it". Never read the value without it.
                // 14/09/2026 : seul EXECUTABLE_NOT_FOUND veut dire « pas de profil, le pilote applique
                // le global ». Tout autre echec (chemin ambigu compris) reste Illisible : jamais un repli devine.
                var trouve = findApp(session, applicationPath, out var profile, ref app);
                if (trouve == (int)NvApiStatus.ExecutableNotFound)
                    return INvApiNative.LectureAuJeu.PasDeProfil;
                if (trouve != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return Illisible;

                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };

                if (getSetting(session, profile, settingId, ref setting) != (int)NvApiStatus.Ok)
                    return Illisible;

                reading = new DrsReading(
                    BitConverter.ToUInt32(setting.currentValue, 0),
                    setting.settingLocation,
                    setting.isCurrentPredefined,
                    setting.isPredefinedValid);
                return INvApiNative.LectureAuJeu.Lu;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return Illisible; }
    }

    public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading)
    {
        reading = default;
        if (!IsAvailable) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var getBaseProfile = _entry.GetDelegate<GetBaseProfileDelegate>(Id_DRS_GetBaseProfile);
            var getSetting = _entry.GetDelegate<GetSettingDelegate>(Id_DRS_GetSetting);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || getBaseProfile is null
                || getSetting is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                if (getBaseProfile(session, out var profile) != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return false;

                // Pre-allocate every ByValArray (golden rule #2) + runtime version word (#1).
                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };

                // GetSetting fills `setting`. A setting absent from the profile returns a non-Ok
                // status (NVAPI_SETTING_NOT_FOUND) → false: honest "not a set value", never a
                // fabricated 0 (the true state is "at the driver default").
                if (getSetting(session, profile, settingId, ref setting) != (int)NvApiStatus.Ok)
                    return false;

                // The DWORD lives in the first 4 bytes of currentValue (recipe §C). The
                // provenance fields come back on the same read — keeping them costs nothing
                // and is the only way to tell a driver-shipped value from a user's choice.
                reading = new DrsReading(
                    BitConverter.ToUInt32(setting.currentValue, 0),
                    setting.settingLocation,
                    setting.isCurrentPredefined,
                    setting.isPredefinedValid);
                return true;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── P0 clock-entry finders ──────────────────────────────────────────────

    /// <summary>Locates the P0/Graphics clock entry's indices in a GET'd pstates struct,
    /// honouring the driver-reported NumPstates/NumClocks. Internal for unit testing; the
    /// write path mutates info.Pstates[p].Clocks[c] IN PLACE using these indices.</summary>
    internal static bool TryFindP0GraphicsClockIndex(NvGpuPerfPstates20InfoV1 info, out int pstateIndex, out int clockIndex) =>
        TryFindP0ClockIndex(info, GraphicsClockDomain, out pstateIndex, out clockIndex);

    /// <summary>Locates the P0/MEMORY clock entry's indices in a GET'd pstates struct,
    /// honouring the driver-reported NumPstates/NumClocks. Mirrors the graphics finder with
    /// the MEMORY PublicClockDomain (4); used by the real memory-offset read. Internal for
    /// unit testing.</summary>
    internal static bool TryFindP0MemoryClockIndex(NvGpuPerfPstates20InfoV1 info, out int pstateIndex, out int clockIndex) =>
        TryFindP0ClockIndex(info, MemoryClockDomain, out pstateIndex, out clockIndex);

    /// <summary>Shared P0 clock-entry finder — locates the first P0 pstate's clock entry whose
    /// DomainId equals <paramref name="domainId"/>, honouring the driver-reported counts. ONE
    /// implementation so the graphics and memory finders cannot drift apart.</summary>
    private static bool TryFindP0ClockIndex(NvGpuPerfPstates20InfoV1 info, uint domainId, out int pstateIndex, out int clockIndex)
    {
        var pstateCount = Math.Min((int)info.NumPstates, info.Pstates?.Length ?? 0);
        for (var p = 0; p < pstateCount; p++)
        {
            if (info.Pstates![p].StateId != PstateP0)
                continue;
            var clocks = info.Pstates[p].Clocks;
            var clockCount = Math.Min((int)info.NumClocks, clocks?.Length ?? 0);
            for (var c = 0; c < clockCount; c++)
            {
                if (clocks![c].DomainId == domainId)
                {
                    pstateIndex = p;
                    clockIndex = c;
                    return true;
                }
            }
        }
        pstateIndex = -1;
        clockIndex = -1;
        return false;
    }

    // ── struct factories: pre-allocate every ByValArray (golden rule), set version ──

    private static NvGpuPerfPstates20InfoV1 NewPstates20Info()
    {
        var info = new NvGpuPerfPstates20InfoV1
        {
            Version = NvApiEntry.MakeVersion<NvGpuPerfPstates20InfoV1>(1),
            Pstates = new NvGpuPerformanceState20[NvApiTuningConstants.MaxPstates],
        };
        for (var i = 0; i < info.Pstates.Length; i++)
        {
            info.Pstates[i] = new NvGpuPerformanceState20
            {
                Clocks = new NvGpuPstate20ClockEntryV1[NvApiTuningConstants.MaxClocks],
                BaseVoltages = new NvGpuPstate20BaseVoltageEntryV1[NvApiTuningConstants.MaxBaseVoltages],
            };
        }
        return info;
    }

    private static NvGpuClientPowerPoliciesStatusV1 NewPowerStatus() => new()
    {
        Version = NvApiEntry.MakeVersion<NvGpuClientPowerPoliciesStatusV1>(1),
        Entries = new NvGpuPowerPolicyStatusEntry[NvApiTuningConstants.MaxPowerEntries],
    };

    private static NvGpuClientPowerPoliciesInfoV1 NewPowerInfo() => new()
    {
        Version = NvApiEntry.MakeVersion<NvGpuClientPowerPoliciesInfoV1>(1),
        Entries = new NvGpuPowerPolicyInfoEntry[NvApiTuningConstants.MaxPowerEntries],
    };

    // ── delegates (all Cdecl, the nvapi64 convention) ───────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPstates20Delegate(IntPtr gpuHandle, ref NvGpuPerfPstates20InfoV1 info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetPstates20Delegate(IntPtr gpuHandle, ref NvGpuPerfPstates20InfoV1 info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerStatusDelegate(IntPtr gpuHandle, ref NvGpuClientPowerPoliciesStatusV1 status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerInfoDelegate(IntPtr gpuHandle, ref NvGpuClientPowerPoliciesInfoV1 info);

    // --- DRS delegates ---
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionDelegate(out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBaseProfileDelegate(IntPtr session, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingDelegate(IntPtr session, IntPtr profile, ref NVDRS_SETTING_V1 setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, ref NVDRS_SETTING_V1 setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int FindApplicationByNameDelegate(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPWStr)] string applicationName,
        out IntPtr profile,
        ref NVDRS_APPLICATION_V4 application);

    // NVDRS_APPLICATION_V4 (NvApiDriverSettings.h). Pack = 8, every NvAPI_UnicodeString is a
    // FIXED 2048-wchar buffer (golden rule #2) — a pointer here would corrupt the layout.
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct NVDRS_APPLICATION_V4
    {
        public uint version;
        public uint isPredefined;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string appName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string userFriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string launcher;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string fileInFolder;
        // bitvector1: isMetro:1, isCommandLine:1, reserved:30.
        public uint bits;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string commandLine;
    }

    // NVDRS_SETTING_V1 (NvApiDriverSettings.h). Pack = 8 (golden rule #2).
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct NVDRS_SETTING_V1
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string settingName;
        public uint settingId;
        public uint settingType;       // NVDRS_SETTING_TYPE (0 = DWORD).
        public uint settingLocation;   // NVDRS_SETTING_LOCATION (0 = current profile).
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        // NVDRS_SETTING_VALUES union, expressed as raw value blobs; the DWORD is the
        // first 4 bytes of currentValue. Pre-allocated to SizeConst (golden rule #2).
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvapiSettingMaxValues)]
        public byte[] predefinedValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvapiSettingMaxValues)]
        public byte[] currentValue;
    }
}
