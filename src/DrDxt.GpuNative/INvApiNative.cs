namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// INvApiNative — the thin managed boundary over the NVAPI native surface.
//
// This is the only gateway to nvapi64.dll from managed code. The entry mechanism
// (nvapi_QueryInterface + Initialize + EnumPhysicalGPUs), the reverse-engineered
// function IDs, the interop structs, and the GET-then-SET write recipe all live in
// NvApiNative. The NON-elevated reader (DrDxt.Detection) and the elevated write
// orchestrators (DrDxt.ElevatedHelper: NvApiTuningActions / NvApiDrsActions) both go
// through this single boundary — no second copy of the function IDs or the write
// recipe (the divergence trap we refuse: the IDs/recipe were validated live).
//
// This mirrors IAdlxNative exactly: ONE native boundary carrying both the reads and
// the writes, replaced by a fake in unit tests (no native calls in tests).
//
// Fail-clean contract (identical to IAdlxNative):
//   • IsAvailable == false  ⇒ nvapi64.dll absent or Initialize failed; every Try* returns false.
//   • Every Try* method never throws; returns false on any fault.
//   • out params are default-initialised on false return.
// -----------------------------------------------------------------------

/// <summary>Thin boundary over native NVAPI. Never throws — returns false on any
/// failure (absent DLL, Initialize failure, missing private function id, driver
/// fault, etc.).</summary>
public interface INvApiNative
{
    /// <summary>True when NvAPI_Initialize succeeded (an NVIDIA GPU + driver present).</summary>
    bool IsAvailable { get; }

    // ── READS (non-elevated; NvAPI_Initialize + GET work without elevation) ───────

    /// <summary>Reads the current applied P0/Graphics core-clock offset (MHz) for the
    /// GPU at <paramref name="gpuIndex"/> (GetPstates20 → P0 graphics delta → kHz→MHz).
    /// Returns false on a bad index, no NVIDIA GPU, or a driver fault.</summary>
    bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz);

    /// <summary>Reads the current applied P0/Memory clock offset (MHz) for the GPU at
    /// <paramref name="gpuIndex"/> (GetPstates20 → P0 MEMORY-domain delta → kHz→MHz).
    /// Part of the same documented GET as the core read (a safe read, no write). The
    /// non-elevated undervolt path needs both offsets to build a faithful revert, so
    /// this is a real read, never a fabricated 0. Returns false on a bad index, no
    /// NVIDIA GPU, an absent memory clock domain, or a driver fault.</summary>
    bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz);

    /// <summary>Reads the current applied power-limit (% of TDP) for the GPU at
    /// <paramref name="gpuIndex"/> (ClientPowerPoliciesGetStatus → entry[0] PCM→%).
    /// Returns false on a bad index, no NVIDIA GPU, or a driver fault.</summary>
    bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct);

    // ── WRITES (elevated-only path; never called during dotnet test) ──────────────

    /// <summary>
    /// Get-then-set: reads the FULL current Pstates20 struct, locates the P0/Graphics
    /// clock entry, verifies it is editable, then submits a MINIMAL write carrying only
    /// that single clock delta (SetPstates20) — never a zero-built struct, never a
    /// voltage edit. <paramref name="offsetMhz"/> is the absolute applied delta (MHz),
    /// converted to kHz for the driver. Returns false if unavailable, the private id is
    /// not exported, the clock is not editable, or any driver fault. Never throws.
    /// </summary>
    bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz);

    /// <summary>
    /// Get-then-set for the P0/MEMORY clock delta — the exact mirror of
    /// <see cref="TrySetCoreClockOffset"/> with the MEMORY PublicClockDomain (4, pinned by the
    /// finder freeze-test) instead of GRAPHICS: reads the FULL current Pstates20 struct, locates
    /// the P0/Memory entry, verifies its editable bit, then submits a MINIMAL write carrying only
    /// that single clock delta. Same SetPstates20 convention as the core write (already
    /// live-validated); never a zero-built struct, never a voltage edit. Returns false if
    /// unavailable, the entry is not editable, or any driver fault. Never throws.
    /// </summary>
    bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz);

    /// <summary>
    /// Get-then-set: reads the driver's [min, max] PCM bounds (ClientPowerPoliciesGetInfo),
    /// clamps the requested percent into that range, reads the current status, mutates only
    /// entry[0]'s power target IN PLACE, then writes it back (ClientPowerPoliciesSetStatus).
    /// Returns false if unavailable, bounds are invalid, or any driver fault. Never throws.
    /// </summary>
    bool TrySetPowerLimitPercent(int gpuIndex, float pct);

    // ── DRS — the SAFE public driver-setting write (shares the NVAPI entry) ───────

    /// <summary>
    /// Applies a single global NVIDIA driver setting (DWORD) via the DRS session sequence:
    /// CreateSession → LoadSettings → GetBaseProfile → SetSetting → SaveSettings →
    /// DestroySession (DestroySession always runs). <paramref name="settingId"/> is already
    /// allowlist-validated by the caller. Returns false if NVAPI is unavailable or any step
    /// of the session sequence fails. Never throws.
    /// </summary>
    bool TrySetDrsGlobalSetting(uint settingId, uint value);

    /// <summary>
    /// Reads a single global NVIDIA driver setting (DWORD) via the DRS session sequence
    /// (CreateSession → LoadSettings → GetBaseProfile → GetSetting → DestroySession). NON-elevated
    /// (a plain read). Returns false when NVAPI is unavailable, the setting is not present in the
    /// base profile (NVAPI_SETTING_NOT_FOUND — the value is at the driver default, never a
    /// fabricated 0), or any step faults. Never throws.
    /// </summary>
    bool TryGetDrsGlobalSetting(uint settingId, out uint value);

    /// <summary>
    /// Same read, but keeping the two fields NVIDIA fills on every DRS read and that the plain
    /// overload throws away: <c>settingLocation</c> and <c>isCurrentPredefined</c>. Without them a
    /// value the driver ships is indistinguishable from a value a human chose — on a machine
    /// carrying thousands of predefined application profiles that makes "what did I actually
    /// change?" unanswerable. Same contract as the plain overload: false on unavailable /
    /// not-present / fault, never throws, never a fabricated 0.
    /// </summary>
    bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading);

    /// <summary>
    /// Reads a setting from the profile the driver ACTUALLY applies to one executable
    /// (CreateSession -> LoadSettings -> FindApplicationByName -> GetSetting -> DestroySession).
    /// The driver hierarchy is Application &gt; current Global &gt; Base (PG-12072-001 p.2), so the
    /// base-profile read alone can report a value the game never sees.
    ///
    /// <paramref name="applicationPath"/> must be the fully qualified path: NVIDIA returns
    /// NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS for a bare file name. When the executable has no
    /// profile of its own the driver returns NVAPI_EXECUTABLE_NOT_FOUND and there is NO automatic
    /// fallback to the base profile — this returns false, and the caller must not substitute the
    /// global value, which would answer a question nobody asked. Never throws.
    /// </summary>
    bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading);

    /// <summary>14/09/2026 — ce que la lecture au profil d'un jeu a vraiment rencontre.</summary>
    enum LectureAuJeu { Lu, PasDeProfil, Illisible }

    /// <summary>14/09/2026 — la meme lecture, qui SEPARE « cet executable n'a pas de profil » (le pilote
    /// lui applique le global) de « le profil existe mais ne se lit pas ». Les confondre laissait
    /// ecrire au global pendant que le pilote appliquait au jeu un profil illisible.
    /// Par defaut : tout echec est Illisible — le cas prudent, jamais un repli devine.</summary>
    LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading) =>
        TryReadDrsSettingForApplication(applicationPath, settingId, out reading)
            ? LectureAuJeu.Lu
            : LectureAuJeu.Illisible;

    /// <summary>PHASE 18 F1 lot 3 — supprime un reglage du profil de CET executable
    /// (NvAPI_DRS_DeleteProfileSetting : « deletes a setting or sets it back to predefined
    /// value »). ELEVATED. Meme sequence que l'ecriture par application ; aucun profil n'est cree,
    /// aucun repli sur le profil de base. false sur toute panne. Par defaut : false — une frontiere
    /// qui ne sait pas supprimer le dit, elle ne fait pas semblant.</summary>
    bool TryDeleteDrsSettingForApplication(string applicationPath, uint settingId) => false;

    /// <summary>L8 suite — le VRR (G-Sync) de l'ecran nomme Win32 (`\\.\DISPLAY1`), lu au pilote.
    /// Lecture, non elevee. false = non lu (pas de NVIDIA, ecran pilote par une autre carte, panne) :
    /// jamais « pas de VRR ». Par defaut : false.</summary>
    bool TryReadVrr(string displayName, out VrrReading reading)
    {
        reading = default;
        return false;
    }

    /// <summary>
    /// L2 — ecrit un reglage dans le profil de l'EXECUTABLE, pas dans le profil de base
    /// (CreateSession -> LoadSettings -> FindApplicationByName -> SetSetting -> SaveSettings ->
    /// DestroySession). C'est la moitie ecriture de la lecture par application ci-dessus : la
    /// hierarchie du driver est Application &gt; Global courant &gt; Base (PG-12072-001 p.2), donc
    /// une ecriture globale peut ne jamais atteindre le jeu qui a son propre profil.
    ///
    /// <paramref name="applicationPath"/> doit etre le chemin complet : NVIDIA rend
    /// NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS sur un nom nu.
    ///
    /// Ne cree jamais de profil. Quand l'executable n'en a pas, le driver rend
    /// NVAPI_EXECUTABLE_NOT_FOUND et cette methode rend false. En fabriquer un serait une
    /// DECISION SEPAREE : le produit ecrirait alors une entree permanente dans la base du driver
    /// pour un jeu que l'utilisateur n'a peut-etre jamais lance, et le revert devrait savoir
    /// supprimer un profil, pas seulement restaurer une valeur.
    ///
    /// NVIDIA documente que la sauvegarde d'une session DRS ecrase ce qu'un autre processus a
    /// modifie entre-temps, sans notification (PG-12072-001 p.11). La session est donc la plus
    /// courte possible : rien entre LoadSettings et SaveSettings que la seule ecriture.
    /// Ne jette jamais.
    /// </summary>
    bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value);
}

/// <summary>One DRS read, with the provenance fields kept.</summary>
/// <param name="Value">The DWORD currently in effect for the profile that was read.</param>
/// <param name="SettingLocation">NVDRS_SETTING_LOCATION: 0 current profile, 1 global, 2 base,
/// 3 driver default (PG-12072-001).</param>
/// <param name="IsCurrentPredefined">Non-zero when the current value is the one NVIDIA ships.</param>
/// <param name="IsPredefinedValid">Whether the shipped value itself was readable.</param>
public readonly record struct DrsReading(
    uint Value, uint SettingLocation, uint IsCurrentPredefined, uint IsPredefinedValid);
