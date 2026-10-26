# DrDXT: verification report

Version française : [RAPPORT-VERIFICATION.md](RAPPORT-VERIFICATION.md).

For someone who has not followed the work: a reviewer, a contributor, a player who wants to know what holds.

**The rule of this page is mechanical**: every "proven" line names a test that exists, and
`python tools/nonuisance/check-guard-tests.py docs/RAPPORT-VERIFICATION.md` refuses a name that does not resolve (CI
runs it on the French version). A report whose evidence cannot be resolved is a brochure.

**What this page does not do**: it does not judge whether a test is GOOD. That is the job of mutation testing. It
establishes that a claim has an instrument, and which one.

---

## 1. What is PROVEN, and by what

| Area | What is established | The instrument |
|---|---|---|
| Hardware non-harm | The only voltage write is the processor undervolt (`SetCpuVoltageOffsetOp`: downward only, down to `TensionCpu.PlancherMv`, volatile, given back at restart). The graphics card only receives clock offset, maximum frequency, power limit and fan, bounded by the allowlist. No operation writes a firmware variable (going to its setup uses `shutdown.exe /r /fw`). The set of operations is **closed**: the exact list is the test's. | `ThePrivilegedSurfaceIsClosedGateTests` · `docs/NON-NUISANCE.md` (the table of write sites, which `tools/nonuisance/count-write-sites.py` recounts) |
| GPU bounds | Core, memory, power limit and fan are bounded, and each bound dies under mutation. | `GpuSafeBoundsTests` |
| Default deny | Registry, services, DRS identifiers and power plans are on an **allowlist**, and a refusal is proven by its REASON, not by its result. | `OpAllowlistTests` |
| Updates | The manifest is Ed25519-signed and checked with the key **actually shipped**; a modified byte is refused; the client-server seam is tested end to end. | `UpdateManifestEndpointTests` · `TheClientReadsTheRealServerTests` |
| Honest screen | No sentence claims a state the product does not compute, and the footer's green tick cannot come back. | `TheScreenMayNotClaimAStateItNeverComputesTests` |
| What a finding says | DrDXT has been free since 23/09/2026: every finding names what it found, none hides the setting. | `FindingViewModelTests.Every_finding_names_what_it_found` |
| Support report | No absolute path, no licence key, no personal data; same memory unit as the screen. | `DiagnosticReportTests` |
| Memory topology | Single channel is said when it is proven; an unreadable slot is SAID instead of kept quiet; nothing is claimed outside a desktop. | `MemoryTopologyAdvisorTests` |
| Memory stability | An active XMP profile plus unclean restarts are reported **without blame** and **without a figure**. | `MemoryStabilityAdvisorTests` · `MemoryStabilityReachesTheScanTests` |
| Memory state of the art | No figure without a source AND a date; the myths are proven ABSENT through the API that would implement them. | `TheRamStateOfTheArtHasNoUnsourcedClaimTests` |
| Instruments | No noise floor without a date and a number of passes; an instrument never run here cannot carry a figure. | `TheInstrumentRegistryCannotRotTests` |
| Server security | No `CommandText` is assembled; every vulnerability advisory has its triage line. | `TheVulnerabilityTriageIsCompleteGateTests` |

The test count is not copied here: the README publishes the last run's, and `tools/gates/compte_des_tests.py` refuses a
count that is not its own.

---

## 2. What is UNKNOWN, named rather than hidden

| What we do not know | Why |
|---|---|
| How many FPS DrDXT gains on a given machine | In-game replay has a noise floor of **6.46 %**: it only resolves a gain above about 13 % (`docs/INSTRUMENTS.md`, I-05). |
| Whether a player's memory profile is stable | We read a configured frequency; we test nothing. |
| The real timings (CL, tRCD, tRP, tRAS) and the UCLK:MCLK ratio | No Windows API exposes them. |
| Whether the 9 BIOS guides are right on a given board | **0 of 9 checked**: it takes the boards. |
| What an unknown firmware does with a boot-to-setup | Out of reach by construction. |
| Whether the product survives a no-boot and its recovery | No hypervisor or sacrificial machine here. |

**The shape of these lines is the product itself**: DrDXT commits to not claiming what it has not measured. A
verification report that hid its own UNKNOWN column would contradict what it verifies.

---

## 3. What awaits a decision

`GATES.md` has the details. One decision remains, and it is not technical:

1. **The code-signing certificate**: without it, the installer triggers SmartScreen.

---

## 4. The most expensive pattern found in this repository

**Three times in one evening, a correct value was computed, documented as decisive, and read by nobody**:
`DeviceLocator` and `Ranks`, then `IsLocked`.

No test can catch it: **a test checks that a value is RIGHT, never that it is READ.** The only instrument that found it
all three times is counting the readers: `grep -rn "<name>" src/ | wc -l`, applied **to the views**, not to the code.

And the corollary, paid the same evening: `SubScores` was covered by **six green tests** and shown by **no view**. A
property that is tested but never shown is worse than dead code: the green suite makes it look alive.
