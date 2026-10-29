# DrDXT security: what can be attacked, and what holds it

Version française : [SECURITE.md](SECURITE.md). The French page is the one the tools check; this translation follows it.

One page, two sections: the **triage of vulnerability advisories** (what comes from outside the repository), and the
**asset x attacker table** (what the product defends itself).

**The rule of this page**: every line of the table carries the name of a test that EXISTS. A line without a test is not
a security line, it is an intention, and an intention gets read as a guarantee. `tools/nonuisance/check-guard-tests.py`
resolves the names cited; the advisory triage is held by `TheVulnerabilityTriageIsCompleteGateTests`.

---

## 1. Advisory triage, measured on 06/09/2026

Raw, complete output: [`vuln-2026-09-06.txt`](vuln-2026-09-06.txt). **Three distinct advisories**, all rated High, all
**transitive** (none is a package the repository asks for directly).

The question that decides is not "what severity" but **"does this code run on the player's machine, and does the
attacker control what must be controlled to trigger it"**. A High CVE in a package that only `dotnet test` loads reaches
nobody; a Medium CVE on the server's data path reaches everyone.

### GHSA-2m69-gcr7-jv3q: `SQLitePCLRaw.lib.e_sqlite3` (CVE-2025-6965)

**REMOVED ON 23/09/2026: no SQLite left in the repository.**

- **Where it was**: `DrDxt.Server`, which held the order database of the payment flow (Lemon Squeezy to Keygen). DrDXT
  became free: the payment webhook, the database and the SQLite package left together. `server/` is not deployed:
  updates go through GitHub Pages (MISE-A-JOUR.md).
- **Measured**: `dotnet list package --vulnerable --include-transitive` on the solution finds no vulnerable package in
  the production projects (23/09/2026).
- **What remains**: the guard `No_server_sql_text_is_built_from_anything_but_a_literal` still refuses any hand-built
  `CommandText`, should SQL ever come back.

### GHSA-7jgj-8wvc-jh57: `System.Net.Http` 4.3.0

**REMOVED ON 17/10/2026: no longer measured.** The test projects moved to `xunit 2.9.3` on .NET 10: the chain below no
longer exists. `dotnet list package --vulnerable --include-transitive` on the whole solution finds no vulnerable package
in any project (`docs/vuln-2026-10-17.txt`). The original triage stays below.

Triage of 06/09/2026: **TEST-ONLY.**

- **Where**: 17 projects, **all `.Tests` projects**; **no production project**.
- **Measured trace**: `xunit 2.5.3` -> `xunit.assert` / `xunit.core` -> `NETStandard.Library 1.6.1` ->
  `System.Net.Http 4.3.0`. The test harness pulls it, not the product.
- **Verdict**: the product code shipped to players does not contain this package. It only runs under `dotnet test`, on
  this machine and in CI. On `net8.0` this package is only a **facade** anyway: the framework's implementation is used.
- **Residual, stated**: a compromised development machine stays compromised; this CVE changes nothing about that, and
  makes it neither more nor less likely.

### GHSA-cmhx-cq75-c4mj: `System.Text.RegularExpressions` 4.3.0

**REMOVED ON 17/10/2026: no longer measured**, for the same reason (`docs/vuln-2026-10-17.txt`).

Triage of 06/09/2026: **TEST-ONLY.** Same trace, same chain (`xunit` -> `NETStandard.Library 1.6.1`), same 17 test
projects, no production project, same facade status on `net8.0`. Same verdict.

---

## 2. Asset x attacker, and the test that holds each line

Each line reads: *if this attacker succeeded, this asset would fall; here is what prevents it, and here is the test
that proves it.*

| Asset | Attacker, and what they control | What holds | Proof |
|---|---|---|---|
| The update manifest | a **hostile server** or a man in the middle | signature checked before the URL is touched | → test: `UpdatePackageVerifierTests` |
| The update binary | a **payload swapped** afterwards | SHA-256 compared with the **already signed** manifest; otherwise never run | → test: `AutoUpdaterTests` |
| The update channel | a man in the middle who **withholds** a security update | https required when mounted; in clear text, a refusal to serve is invisible to a signature | → test: `TheUpdaterIsMountedOrSaysWhyNotTests` |
| The client-server seam | a **drift** between the two canonical forms, which would refuse everything silently | the real signer signs, the real verifier verifies, in one test | → test: `TheServerSignsWhatTheClientVerifiesTests` |
| The server's secrets | someone who **decompiles** the client | no secret in `src/DrDxt.*`; static sweep of the tree | → test: `SecurityGateTests` |
| The trust layer | a `catch` that would return **success** on a broken signature | static anti-fail-open check on updates | → test: `SecurityGateTests` |
| The privileged named pipe | an **unauthorized client** that connects to it | the caller is authenticated before any handshake, default deny; fixed on 26/09/2026: this line used to cite the test of `PrivilegedPipeServer`, a server the product never starts, instead of the real server's | → test: `HandshakeChannelIntegrationTests` |
| The machine's registry | a **forged** privileged op on the pipe | leaf-exact allowlist, plus 11 subtrees always refused | → test: `OpAllowlistTests` |
| The player's machine | **one more op** added by mistake later | frozen surface: the exact list of op names lives in the test, not here (a figure written by hand drifted from 18 to 38) | → test: `ThePrivilegedSurfaceIsClosedGateTests` |
| The hardware | a **voltage write** added later | no graphics card voltage; a single voltage exists, Intel processor core and cache, downward only (`SetCpuVoltageOffsetOp`, 0 to −125 mV); the name filter catches any other voltage op | → test: `NoVoltageWriteOpExistsGateTests` |
| The BIOS | the component that **guides** through the BIOS, if it wrote | `DrDxt.Bios` only references `DrDxt.Core`: structurally unable | → test: `TheGuideNeverWritesTheBiosGateTests` |
| The reconciler's desired state | a file **tampered with** on disk | signature checked, loading is **fail-closed** | → test: `SignedDesiredStateStoreTests` |
| The support report | what the player **pastes** into a public ticket | profile, PC name, addresses, serial numbers and emails removed | → test: `ExportDeMachineTests`, `CartesAgirTests` |
| The installer | shipping an unsigned binary **believing it signed** | without `-Sign`, the installer is named `DrDXT-Setup-NON-SIGNE.exe`; `-Sign` without a certificate fails at once, with no fallback to unsigned; a signature that was applied is read back before being announced. An unsigned installer ships today (the certificate is not bought): its name says so | → test: `TheInstallerCannotShipUnsignedGateTests` |
| The install folder during the copy | a **non-elevated** program of the installing account that **replaces** the service host, PresentMonService, the helper or the launcher during extraction: the files laid down belonged to it, and CREATOR OWNER of Program Files gave it full control | the folder is **locked before the copy** (`PrepareToInstall`): owner Administrators, explicit rules removed, protected inheritable DACL (SYSTEM and Administrators full control, Users and OWNER RIGHTS read); read back as an allowlist (no write outside SYSTEM and Administrators); a failure installs nothing. After the copy, the lock is read back, then the build-time SHA-256 of each executable run as SYSTEM or elevated (and of its DLL) is read back before `sc start` and `schtasks` | → test: `LeDossierEstVerrouilleAvantLaCopieTests` (the script, read as text); gate `tools/gates/installeur.py` (the compiled script, with stand-in files: it checks neither the payload, nor the hashes, nor the Pascal that runs); `installer/build-installer.ps1` refuses a binary that carries the developer's path and a PresentMon whose hash or Intel signature does not match. The real run, installer rebuilt then installed, remains a step on a real machine |
| The staged installer | it is **replaced** between the hash check and the click, in a folder the user can write, then run **under UAC** | the binary is **locked** (read-only share) from the hash check until after launch | → test: `VerificationMiseAJourTests` |
| The privileged named pipe, app side | a process of the same account that **takes the pipe's name** before the helper and answers "applied" | before the handshake, the app compares the server's image with the helper installed next to it, then requires its rights: the pipe belongs to Administrators or SYSTEM (the elevated helper gives itself that; a non-elevated squatter cannot, Administrators is "deny-only" in its filtered token) and the server's token, if it can be read, is elevated. The image alone let through the real helper relaunched non-elevated by a program of the account. The real crossing (medium-integrity app, elevated helper) is measured with `tools/helperhandshake` | → test: `TubeUsurpeTests`, `PrivilegeDuServeurDuTubeTests` |
| The pipe, installed app side | a program of the account that sets `DRDXT_HELPER_SESSION_KEY` in `HKCU\Environment` so the app takes the legacy client, which does not check the server | installed under Program Files, the app ignores this key and always goes through the handshake (26/09/2026); the wiring is not testable outside an install, only the rule is | → test: `ElevatedClientFactoryTests` |
| The helper's availability | a program of the account that **connects to the pipe and stays silent** (single instance): nothing can be applied or undone anymore | 10 s to speak (handshake and request); the silent connection times out without stopping the service (26/09/2026) | → test: `HandshakeChannelIntegrationTests` |
| Active leases | a helper shutdown (normal, or caused by a pipe squatter) that would leave a cap in place until restart | at shutdown, every active lease is given back through the same path as expiry (26/09/2026) | → test: `LeaseWatchdogTests` |
| The privileged named pipe, from the network | a **network session** (SMB, same account or administrator) that passes the ACL: .NET does not set `PIPE_REJECT_REMOTE_CLIENTS` (source of `NamedPipeServerStream.Windows.cs`, dotnet/runtime) | explicit deny of the NETWORK SID in the ACL, evaluated before the allows; local connection stays allowed, measured; refusing a real remote client is not measured for lack of a second machine (26/09/2026) | → test: `LeTubeDuHelperRefuseLeReseauTests` |
| The installed helper's mode | `DRDXT_HELPER_SESSION_KEY` set in `HKCU\Environment` to switch it to legacy mode, which the app no longer speaks | installed, the helper ignores this variable; shared rule `EstInstalle` (26/09/2026) | → test: `HelperAuthenticatorFactoryTests` |
| The data SYSTEM trusts (`ProgramData\DrDxt`) | a program of the account that **creates the folder in advance** (ProgramData lets accounts create there) and puts a junction (`temoins` to System32) or a trap file (`pause-du-maintien`, journal) in it | the installer creates and hardens it **before the copy**: root refused if it is a junction, owner Administrators, DACL from `TrustedManifestRoot.BuildHardenedSecurity`; a junction child loses its link without following it, an ordinary child loses the rules set by the account and inherits from the root, except the private key, **without changing owner**: set by the account, it stays untrusted and its readers (`EnsureTrustedFile`) ignore it; result read back. Accepted limit: a file set by the account with a DACL that denies Administrators cannot be reset, the read-back sees it and the install is refused (`DonneesImpossibles`): a refusal to install, not an elevation. Uninstall no longer empties `temoins` blindly (`SupprimerLesTemoins`) | → test: `LesDonneesSontVerrouilleesAvantToutTests` |
| The temp folder of elevated processes | a program of the account that prepares, in `%LOCALAPPDATA%\Temp`, a file or junction that an elevated child (dism, the helper's PowerShell, logman and the elevated app's `.etl`) would open | the launcher gives elevated processes `TEMP`/`TMP` = `Windows\SystemTemp` (SYSTEM and Administrators only) if it is safe, otherwise `ProgramData\DrDxt\temp` created under a safe `ProgramData\DrDxt`, otherwise `Windows\Temp`; never the account's folder. Safe means: no junction or link up to the root, owner and only writers SYSTEM, Administrators or TrustedInstaller | → test: `DossierTemporaireEleveTests` |
| Programs started by the helper | an executable of the same name put in the current folder, started **elevated** if the helper called it by name alone | every launch goes through an absolute path to System32; a guard refuses a bare name | → test: `AucunExecutableLanceParSonNomNuTests` |
| The installed helper | a program of the account that **starts it itself** with `--app-path` pointing to its own executable, under a UAC prompt in DrDXT's name | installed under Program Files, the helper only trusts executables in its own folder (26/09/2026) | → test: `HelperAuthenticatorFactoryTests` |
| Startup replay | a **trapped** `pending-revert.json` (account folder) that files a GPU write under a registry lever | each lever only replays the op types it writes itself (26/09/2026) | → test: `RevertPiegeAuDemarrageTests` |
| BCD undo | an undo guard **modified** to set another allowed BCD element | the guard is only read back for `bootstatuspolicy`, the only element the card removes (26/09/2026) | → test: `CartesAgirTests` |
| Allowed registry values | a **forged** write to an allowed leaf, with another type or value (a 16 MB page file, or set off a disk) | each leaf imposes its type, and `PagingFiles` only accepts automatic management and the empty value its Undo puts back (26/09/2026). Remaining: a trapped replay naming the page-file lever can put back that empty value, the original state | → test: `OpAllowlistTests` |
| Startup replay, per leaf | a **trapped** `pending-revert.json` that files another leaf's write under a registry lever (HAGS carrying "empty `PagingFiles`") | each lever only replays the leaves its code writes: hive, path and name; each NVIDIA lever only its own DRS settings (a frame limiter filed under "prefer performance" is refused); each AMD lever only its own ADLX kind (the frame queue no longer replays a power limit) (26/09/2026) | → test: `RevertPiegeAuDemarrageTests` |

---

## What this page does not say

- It does not prove that **no** other attack path exists. It lists those that are known and guarded; **UNKNOWN stays
  UNKNOWN**.
- It does not judge the QUALITY of each guard. That work is elsewhere: the non-harm guards are proven by mutation in
  [`NON-NUISANCE.md`](NON-NUISANCE.md), one by one.
- It does not cover what is not shipped yet: the installer has installed DrDXT at each release since 03/10/2026, but
  **uninstall has never been verified** and **the certificate is not bought**: the real surface on a player's machine is
  therefore not fully observed. These two lines live in `GATES.md`.
- **Known residual, unguarded**: a trapped `pending-revert.json` can still make a lever replay its own original value,
  chosen by the attacker: a power limit between 50 and 100 % (the allowlist accepts 0 to 100, the executor refuses below
  50, `NvApiTuningRailTests`), a disabled page file, a low frame limit. Since 26/09/2026 it can no longer make another
  lever's target replay (`RevertPiegeAuDemarrageTests`).
- **The boundary, stated once**: a program running under the user's account can write that file, but it can also inject
  itself into the app's process (same account, same integrity level) and ask the helper whatever the app can ask. The
  real barrier is therefore the helper's allowlist, which bounds everything it can be made to do, not the file. For
  that reason, moving this journal into the helper's root was ruled out on 26/09/2026: several days of work for a
  barrier that injection bypasses.
- Fixed on 26/09/2026: this section used to describe the pipe authentication as "one-way", failing open. The guard had
  been in place since 11/09; the line now lives in the table.

## 3. Review of 08/10/2026: fixed, and accepted limits

Fixed that day:
- A lease lasts no more than 600 s per renewal, and a caller holds no more than 8 (`BauxPlafonnesTests`): without a
  cap, a caller kept the administrator helper alive forever.
- The helper started on demand stops after 15 minutes without an authenticated connection or a lease
  (`ArretSurInactiviteTests`); a refused connection no longer keeps it alive.
- Session values (HKCU) are written with the player's token, after the same allowlist (`ClientSessionUtilisateurTests`).
- The legacy pre-shared-key mode no longer exists in a Release build.
- The service-state temp file is deleted if it exists, then created exclusively.

Accepted limits, stated:
- The helper trusts the app's image (path + SHA-256), which runs at medium integrity: a program of the same account
  that injects itself into the app can, while the helper lives, run the allowlist's ops. The allowlist bounds everything
  (no arbitrary code execution); the idle shutdown narrows the window.
- Uninstall, run elevated, also gives back the kept HKCU values: with a UAC prompt answered by another account, it would
  write that account's hive. Refusing them to the helper would prevent giving them back in the common case.
- ~~On AMD, the maximum frequency is bounded by the driver's range, not by a margin of the helper's own.~~ No longer
  true since 17/10/2026: the helper keeps it in its own band (section 5).
- No anti-replay counter in the signed manifest: replaying an old manifest requires writing to a folder reserved for
  administrators.
- A program of the account can squat the pipe name between two connections: denial of service, the app refuses the
  squatter.

## 4. Review of 10/10/2026: the program that receives elevation is native

Found by an independent adversarial review: a process elevated by UAC receives its environment from
`HKCU\Environment`, which any program of the account writes without rights. The .NET runtime reads there, before the
program's first line, what it needs to load a DLL of its choice (`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER_PATH`,
documented by Microsoft; `StartupHookSupport=false` only cuts `DOTNET_STARTUP_HOOKS`). Every "Yes" given to DrDXT would
have run that code as administrator, inside the helper's genuine image.

Fixed: the only DrDXT executable that receives elevation is `DrDxt.Lanceur.exe`, compiled natively (NativeAOT, no
CoreCLR: these variables have no hold). It only starts DrDXT's programs, next to it, with an environment rebuilt from
Windows APIs (`EnvironnementPropre`: SystemRoot, PATH limited to Windows, profile folders read from the token): nothing
from the received environment gets through. The helper (`RunAsProcessLauncher.Cible`), the app relaunching itself
elevated (`RelanceElevee`) and the validation zip go through it; the installer ships it
(`LeLanceurNatifRecoitLElevationTests`). Measured on 10/10: started by the launcher with `CORECLR_*` and `DOTNET_*` set,
the child program sees none of these variables (13 variables, PATH = Windows only).

Also fixed: `StartupHookSupport=false` on the service; an update download is capped at 400 MB.

Limits restated by this review, accepted:
- What a program of the account obtains through the allowlist, without code execution: restart into the BIOS, BCD
  options, upload cap. Reversible, bounded.
- The helper must never be distributed outside the installer: outside Program Files, it trusts the image passed in
  `--app-path`, and the account can replace it before elevation. Only the installer is published.

## 5. Audit of 17/10/2026: "DrDXT never does harm"

A reading of everything that writes on the machine, against a hostile program of the player's account (which can
inject itself into the app and talk to the helper, section 3: only the allowlist and the executor's rails bound it).

Fixed:
- **Elevated PowerShell, the account's modules.** PowerShell 5.1 puts `Documents\WindowsPowerShell\Modules` first in
  `PSModulePath`, even elevated, and the account writes there without rights (a OneDrive folder even syncs there from
  another device). Measured on the reference machine: a stand-in module exporting `Checkpoint-Computer` from there came
  before Windows'; the helper would have run it as administrator, without a prompt, before each batch. The restore
  point now gets a `PSModulePath` reduced to Windows modules, imports the module by its System32 path and stops if the
  command comes from elsewhere (`ModuleCheckpointPiegeTests`); the launcher sets the same path for the repair console
  and every program it elevates (`EnvironnementPropreTests`).
- **Undos read from an account file.** "Undo", automatic removal, the trial and card monitoring replayed through the
  helper the op read from `leviers-appliques.json`, `reglage-carte-retenu.json` or `preuve-repos-carte.json`, unfiltered:
  writing a file made any allowlist op run on the first click. The startup check (types and leaves written by each
  lever) now applies when those three files are read (`AnnulationsLuesDansUnFichierTests`).
- **Processor power limit.** A raise was only bounded by the registry's PL2, often 0x7FFF (4095 W): it is now bounded by
  the published maximum power (0x614), and refused under an "unlimited" PL2 with no published maximum. Turning PL1 off
  through its enable bit (the processor then held PL2 forever) is refused, except to give back exactly this boot's
  firmware value (`PawnIoCpuPowerActionsTests`).
- **AMD undervolt.** MaxFreq stays within the app's cautious band around the boot value, on top of the driver's range,
  and it is read back. Giving back beyond the band (an undervolt kept across a restart) never writes the requested
  frequency and never resets to factory on its own: the helper refuses and says so. The factory reset is a separate
  action, never the return of a lease nor replayed at startup (`ADLXTuningActionsTests`, `LeaseWatchdogTests`,
  `RevertPiegeAuDemarrageTests`); a program of the account can send it itself, never go beyond the driver's default.
- **Uninstall through the launcher.** `[UninstallRun]` started the .NET helper directly, elevated, with the account's
  environment (`CORECLR_PROFILER`…): it now goes through `DrDxt.Lanceur.exe`, which waits for it to finish in this mode;
  the restore-point PowerShell also removes the runtime variables (`DesinstallationInstalleurTests`,
  `EnvironnementPropreTests`).
- **Verification report**: nothing from the report or the hardware in the ticket URL anymore, which the browser sends to
  GitHub as soon as it opens (`LienDeRapportTests`).
- **Registry type**: an original REG_SZ, REG_QWORD or REG_EXPAND_SZ is no longer taken for a DWORD or a string, which
  the undo would have rewritten in another type (`TypeDuRegistreConserveTests`).
- **Startup service**: the daily pass waits for the end of a match (`WorkerTests`).
- **Origin of the removed BCD.** `GardeDuBcdRetire` writes it and flushes it to disk before bcdedit, and erases it if
  the helper refuses; the screen goes through it since 70a4fd8a, and an impossible guard removes nothing
  (`GardeDuBcdRetireTests`).
- **Updates.** Startup only reads the signed manifest (`AutoUpdater.AnnoncerAsync`); the installer is only downloaded
  on the "Install" click (`TelechargerAsync`), wired since e3d5d3a5 (`AutoUpdaterTests`, `MiseAJourLenteTests`).
- **Lease heartbeat without a prompt.** The heartbeat never starts the helper (`IElevatedClient.SansRelance`): refused,
  a Windows prompt came back at every heartbeat, in game included (`BattementSansInviteTests`). With the helper
  unreachable, the fixed fan is given back to automatic in a single attempt, and the screen says whether it stayed fixed
  (`EssaiRenduLibereLeBailTests`).
- **AMD factory reset on click.** `AnnulationParClic` sends the factory reset on "Undo", or on the startup finding's
  button, when the helper asks for it; a successful reset counts as "given back" (`AnnulationParClicTests`). Wired into
  the screen since 23118e76: only the click triggers it. Startup and the incident guard, which give back without a click
  (`AnnulerLevierAsync(id, parClic: false)`), report the helper's refusal and never reset the card to factory.

Fixed: **the "DrDXT releve" scheduled task** (limit R-H, then H-1 of the second audit). It ran at the highest level
(`/RL HIGHEST`, to read the processor temperature through PawnIO): the elevated app then wrote in `%LOCALAPPDATA%\DrDXT`,
where a program of the same account without rights can put a junction, and each logon became a file creation,
overwrite or deletion as administrator; it also advanced the alternating trial without a prompt. The task now runs
without rights, for the account that logs on (principal of the Users group at LeastPrivilege, whatever account answered
the UAC prompt at install), starts the app directly (the launcher, not elevated, would ask for elevation), never starts
the helper (`CreateSansLancer`) and sets or gives back no lever, except the alternating trial's, and only through a helper
that is already running, never through a prompt. An update replaces the task of a previous install
(`/Create /F`). What it loses: without the DrDXT service, a match played with DrDXT closed has no processor temperature or
state, and the `logman` fallback of the frame counter (an ETW session, reserved for administrators) no longer opens in
the background (`ReleveSansLAppTests`).

Chosen by the player: **the "DrDXT helper" scheduled task**, unticked by default. Offered only when the account that started
the installer (the session's, read through WTS, not the administrator who approves UAC) is an administrator; when it signs
in, at the highest level, it starts the helper through the launcher (rebuilt environment) with `--app-path` and `--service`:
alternating trials move forward with DrDXT closed. The helper's pipe then stays open for the whole session instead of
closing with the app or after 15 minutes without a connection. Nothing else changes: `--service` only replaces the
inactivity stop with a stop while the installer runs, leases given back (mutex `DrDxtInstalleur`; a program of the
account that creates it can only stop the helper). Caller authentication (path and SHA-256), the allowlist and the
executor are those of the on-demand helper. Removed when unticked, on an update before the copy, when the folder is not
protected and when uninstalling (`LAssistantDeSessionSurChoixTests`, `LeModeServiceNeChangeQueLArretTests`).

Fixed: **undo journals flushed to disk** (M-4 of the second audit). `FichierJsonAtomique` writes its temp file with
WriteThrough and flushes it before the rename: a power cut no longer leaves an unreadable `leviers-appliques.json`
(`FichierJsonAtomiqueTests`). An unreadable journal, from a power cut or because a program of the account wrote it, no
longer stops the journal silently: the applied-levers one is reported at the scan then set aside at the next setting,
the session one is reported at startup then set aside instead of being overwritten or deleted
(`JournalDesLeviersAppliquesTests`, `RevertEnAttenteIllisibleTests`). Known limit: Optimize keeps a lever's origin after
setting it (the snapshot is born from the setting itself, `TweakEngine`); a power cut between the two leaves the lever
set without "Undo". The only case with no way back through Windows, the BCD, is guarded before the write
(`GardeDuBcdRetire`).

Partly fixed, residual accepted: **the alternating trial reads an account file** (M-2 of the second audit).
`essai-alternance.json` lives under the profile, which any program of the account can write. "Stop the trial" no longer
sets or gives back a lever that is not allowed to alternate, and the trial neither sets nor gives back anything while a
game is running, the origin to give back after a reboot included (`EssaiEnAlternanceTests`); only "Stop the trial", a
click, gives the lever back during a game. Residual: a forged file can make a hot-switchable lever (AutoHot) be set
without a click, between two matches, within its bounds. The boundary remains the helper's allowlist and its bounds: a
program of the account can drive the app anyway.

Fixed, not validated on hardware: **the ADLX ratchet from one boot to the next** (M-1 of the second audit). The
+100/−200 MHz band was measured around the MaxFreq read at the first pass of each boot: if the driver keeps the value
across a restart, +100 MHz per boot up to the overclock ceiling. The origin is now kept per card in
`HKLM\SOFTWARE\DrDXT\Helper\AdlxOrigine` (written by the helper only, removed at uninstall), with the last value written
by DrDXT, recorded before each write: at the next boot, a MaxFreq equal to that value keeps the origin, any other
(set in AMD Software, or reset to factory) becomes the origin (`AdlxOrigineEntreDemarragesTests`). Limits: the identity
is the ADLX index and the PNP identifiers of the AMD cards (a card added or removed restarts from the value read); and if
ADLX accepts this write without elevation, as AMD Software does, the helper is not the boundary for AMD. When the set of
AMD cards seen changes (integrated chip enabled, card removed), the origin restarts from the value read, which the driver
may carry above the default; changing that set takes a hardware action or rights.

Fixed: **the page file disabled by a compromised caller** (M-5, point 1, of the second audit). The allowlist accepted an
empty `PagingFiles` (the Undo of `fixit.sane_pagefile` gives back an empty origin) and any drive letter: at the next
restart, no page file. The helper's registry writer, which all three executors go through, now only writes the empty
value if the current value is already empty or if the helper recorded, when writing over it, that the origin was; the
record keeps the value DrDXT set, and the empty value is only accepted if that value is still in place (the player has
set their page file since: empty refused, record removed); it survives restarts and is erased on give-back. Residual:
while DrDXT's setting stays in place, giving back the original empty value stays possible, like the player's Undo. An
entry on a named drive must target a fixed drive that exists (`GardeDuFichierDEchangeTests`).

Fixed, residuals stated: **the app relaunched as administrator under an account folder** (residual of the secu-2
review). "Relaunch as administrator" and `--analyser` make the app elevated, after a click and a "Yes"; it writes,
renames and deletes under `%LOCALAPPDATA%\DrDXT`, which any program of the account can modify. A junction set in advance
(to `\RPC Control` and object links, or to a Windows folder) turned that "Yes" into a write or deletion as administrator
elsewhere. At elevated startup, before any write, the app creates the subfolders it uses (`diagnostics`, `logs`), checks
without following links that neither the folder, nor a subfolder, nor a file is a reparse point or a hard link, then
holds the folder and its subfolders open without delete sharing until it closes: none can be renamed or replaced by a
junction. If a link is found, it does not start and says so (`DossierDeLAppEleveTests`, on real junctions and real hard
links). Residuals, after the check: a file symbolic link, which Windows only lets you create without elevation with
developer mode on (Microsoft Learn, `CreateSymbolicLinkW`, `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`); a file
replaced by a hard link, for which Microsoft documents no access requirement on the target file (`CreateHardLinkW`): a
direct write by the elevated app to that name would go into that file. Atomic writes (temp file then rename) replace
the entry without writing through the link. The exported machine and the "Check DrDXT" report go under Documents,
outside the held folder: elevated, the app refuses a junction or symbolic link on Documents and every parent up to the
profile (reparse tag read without following the link; OneDrive's cloud-files tag, seen on the reference machine, is
allowed), and creates the file without ever opening a name that already exists; a name already taken is reported,
nothing is deleted. The tag is read on handles opened without delete sharing, held until the creation ends: between the
check and the write, none of these folders can be renamed or replaced by a junction (`EcritureDansDocumentsTests`). Two
more layers close what the held folder does not see: a parent of the data folder, and a subfolder created after the
check (the `staging` of an update downloaded later). Every elevated DrDXT process (the elevated app, the helper and its
uninstall, the SYSTEM service) sets Windows' RedirectionGuard protection at startup (`ConfianceDesRedirections`,
`SetProcessMitigationPolicy`, EnforceRedirectionTrust): it no longer follows a junction or link created by a
non-administrator account; Windows 10 22H2 and 11 only, otherwise it is written to the log. And everywhere, elevated, no
write, append or set-aside by `FichierJsonAtomique`, nor the staging purge, its writes, the BCD guard, the copy of
signed data or the removal of the trial journal, crosses a reparse point, from the root folder to the file
(`FichierJsonAtomiqueTests`, on a real junction; without elevation, an account junction is still followed). Residual: a
component created between this check and the write, which only RedirectionGuard closes.

Fixed, residual stated: **the upload cap kept for the SYSTEM service** (M-5, point 2, of the second audit). Once kept,
it is set by the service at each session, without DrDXT open and without a prompt, as soon as the named exe runs. The
helper only required a plausible game name: a program that gave its own name got the whole machine's upload throttled
and its own exemption, restarts included. Only a game recognized by its name (`NomExeDeJeu.EstUnJeuConnu`, a closed list
of the most played competitive games) is now kept, and the service no longer replays a cap kept for another name;
another game keeps the app's protection, under a lease, while it is open (`PlafondRetenuPourLeServiceTests`,
`PlafondEnvoiDuServiceTests`, `CartesAgirTests`). Residual: a program named like a recognized game (`cod.exe`) still gets
the throttle while it runs. No throughput floor: a legitimate ADSL upload can be under a few Mbit/s. Recognition is by
name only, neither path nor signature.

Fixed: **the fixed fan without a lease** (M-5, point 3, of the second audit). `ApplyGpuFanCurveOp` went through without a
lease; the 80 °C thermal guard and expiry only see leases: a forged fixed speed stayed until restart while the card
heated up, and the promise "never a fixed speed above 80 °C" was false for it. The helper's server now only sets it under
an unexpired lease of the same caller whose return gives this fan back to automatic (`LeaseIpcPipelineTests`).

Fixed: **the frame limiter in the base profile** (M-5, point 4, of the second audit). The allowlist accepted in the base
profile any value documented by NVIDIA, including a 1 fps limiter, and the global write was not read back. In the base
profile, a limiter under 20 fps is refused (rule shared with the levers, `ValeursDrs.Admise`), and the global write is
read back in a new session: a value the driver did not keep is a failure (`ValeursDrsBorneesTests`,
`NvApiDrsActionsTests`). Forced vertical sync in the base profile stays allowed (reversible in the NVIDIA panel).

Fixed: **an antivirus or parental control switched off at startup** (M-5, point 4). Only Windows Security was protected
in StartupApproved: a compromised caller could switch off a third-party antivirus's interface or a parental control. A
shared rule (`ProtegesAuDemarrage`, name fragments in a closed list) protects them in the helper's allowlist, the
finding and the doctor's actions; turning them back on stays allowed (`StartupApprovalAllowlistTests`,
`ProgrammesAuDemarrageTests`). Security software missing from the list can still be switched off.

Product choices, stated: **what the bounds still allow** (M-5, point 4). A compromised caller of the helper gets, within
the bounds: −125 mV on the processor (the absolute bound holds, but most Raptor Lake chips crash at that value, until
restart: the voltage is volatile); a PL1 up to min(PL2, registry ceiling 0x614) on up-to-date microcode (100 °C held on
the reference machine, PROCHOT protects the silicon); three closed BCD elements (`useplatformclock`, `tscsyncpolicy`,
`bootstatuspolicy`), whose effect under BitLocker (recovery asked at the next boot?) is not measured (GATES). These
values are levers of the product: removing them would remove the levers.

## 6. BIOS guide on the phone: accepted limit

The BIOS guide QR code opens `docs/guide-bios/index.html` (GitHub Pages), with the guide in the URL fragment, which the
browser never sends. Anyone can forge such a URL and post it as a QR code.

**The page cannot prove that a QR code comes from DrDXT; it only says what it already carries, under a warning.** The
URL (format 2) only carries codes: each instruction, fallback line or note, each menu value and each key is a code from a
closed list written in the page. The values and keys are those composed by the models of `guides.json`, copied word for
word (a test keeps them equal); a value or key in clear text, or an unknown code, gets the whole URL refused. The page
refuses any other version (version 1 was never issued). No line says "verified"; a fixed line at the top says to only
follow the QR code scanned from DrDXT. There is no signature: a key in the app would be public.

What still gets through: the board name, the only field in clear text, because it comes from the hardware. It is
bounded (120 characters, letters, digits and `().,&/+_-`, no address and no number followed by a voltage unit, whatever
the case) and the page shows it as data, "Motherboard detected by DrDXT: …", never as a title or an instruction. A
sentence forged in that place, "Set CPU Core Voltage 1.650 then save", is therefore shown as a board name.

The tests run the page's own script (Node, `PageDuGuide.harnais.mjs`): a forged value, key and board, a format 1 URL
(`GuideBiosParLaPageTests`). The app applies the same lists and keeps the QR code as text for a guide the page would
refuse.

## 7. Windows credentials and proxies: accepted limit

The app's HTTP clients (update check at startup, signed tables, line test) passed the account's credentials to any
proxy that answered 407. On a hostile network (public Wi-Fi, event LAN), a fake proxy published through WPAD thus
received the account's NTLM exchange at every launch, without a click: enough to relay it or crack it offline. Waiting
for a 407 changes nothing, since a fake proxy answers one.

**Credentials only go to a proxy someone configured**: a valid HTTP_PROXY, HTTPS_PROXY or ALL_PROXY variable (.NET then
no longer uses the system proxy), or a manual proxy when neither automatic detection nor a PAC script is active. With
detection or a script, the proxy is chosen host by host (the download is redirected to another host than the
manifest): the decision is therefore about the configuration, not an address. An unreadable configuration gives
nothing (`ProxyDuCompteTests`, with a fake local proxy that requires NTLM).

Accepted limit: behind a corporate proxy through automatic detection (WPAD) or a PAC script that requires
authentication, and behind a manual proxy while "Automatically detect settings" stays checked, the update check stays
"unreachable". Unchecking detection, or setting HTTPS_PROXY, brings it back.

## 8. Undated manifests: accepted limit

The signed manifests (app update, signed tables) carry neither a publication date nor an expiry. A compromised mirror
or a network attacker, unable to sign, can serve an old valid manifest again, or answer 404: the app then says "up to
date" and the player stays on their version without knowing. **That is a possible freeze, never a rollback**: a version
older than the installed one is refused (`UPDATE_DOWNGRADE`), and so are tables older than those in use. The fix (dated
manifests, re-signed at least every 30 days) would require a monthly re-signature with the release key; it is described
in GATES.md (§7), waiting for a CI that holds the key.

## 9. DLLs placed next to the installer: finding examined

The "Install" button copies the verified installer into the staging folder, under `%LOCALAPPDATA%` (writable by the
account), locks it for reading and starts it through the shell. The lock protects the exe, not its neighbours: the
question was whether a DLL placed next to it would be loaded as administrator.

**Checked** (without running it, on the 0.9.1 setup, Inno Setup 6.7):
- the setup's manifest asks for `asInvoker`: `DrDXT-Setup-*.exe` runs without elevation. A DLL placed in the staging
  folder would be loaded with the account's rights, the ones already needed to put it there: no gain. The manifest
  declares a level, so Windows' installer-detection heuristic does not apply;
- its static imports are advapi32, comctl32, kernel32, oleaut32 and user32;
- the elevated part is `setup.tmp`, which Inno extracts into its own temp folder (`is-` followed by 10
  cryptographically random characters) then relaunches under UAC, not from the staging folder. Inno 6.7 announces extra
  protections against DLL preloading.

**Not checked**: the real DLL search of elevated `setup.tmp`. It takes Procmon during a real install, hence a UAC click:
the step is in GATES.md §1.

**Residual**: Inno's temp folder is under the account's `%TEMP%`. Code already running under this account can target
`setup.tmp`, as with any Inno installer; Microsoft does not treat the UAC prompt of the same administrator as a security
boundary.

**Closed: the already elevated app.** Relaunched as administrator, the app would have downloaded and prepared the
installer as administrator in the account's folder (a junction set in advance redirects the write there), then passed
its elevated token to the setup, which would have loaded whatever is placed next to it. In an elevated app, the
"Install" button stays silent: nothing is downloaded, the version stays named with "Relaunch DrDXT normally to install
the update" (`Une_App_elevee_ne_telecharge_rien_et_dit_de_relancer_normalement`). Launching refuses it too, before any
copy, without removing the version (`Une_App_elevee_ne_lance_pas_l_installeur`). Consequence, stated: on a PC where UAC
is disabled, every process of an administrator is elevated, and the button always refuses; the installer is then
downloaded by hand.

## 10. AMD undervolt at uninstall: known limit, not validated on AMD

Uninstall gives back what the signed manifest carries, and puts an NVIDIA card back to automatic fan and a 0 clock
offset (`RendreLaCarteNvidiaAsync`). An AMD undervolt (`ApplyAdlxTuningOp` of type `Undervolt`) does not go into the
manifest: its origin is not one uninstall knows how to give back (`OrigineRendable`). If the AMD driver keeps it after a
restart, it stays set after uninstall, until a reset in the AMD software. The real behavior at restart has not been
measured, for lack of an AMD card. An alternating trial never sets it: an origin neither givable back nor reset to
automatic by uninstall is given back at once
(`Un_levier_que_la_desinstallation_ne_rendrait_pas_n_est_jamais_essaye`).

## 11. Leases tied to the app's process: accepted limit

A lease (Expert fan, card offset, upload cap) belongs to the process that set it up: its pid, resolved through the
pipe, after the app's authentication (`LeaseOwner`). Only that process renews or closes it. A program of the account
that injects itself into the app can therefore do so too, as it can do anything the app can: that is the accepted
injection model of section 3, which only the allowlist and the executor's rails bound. A pid reused after the app dies
is not enough: the new process must first pass the app's authentication (image and hash), and the orphaned lease
expires meanwhile, given back by the watchdog (`LeaseWatchdogTests`).
