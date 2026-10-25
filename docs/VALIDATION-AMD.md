# To validate on AMD

Version française : [VALIDATION-AMD.fr.md](VALIDATION-AMD.fr.md).

DrDXT has no AMD machine. Everything that touches a Ryzen processor or a Radeon card is written and tested on data,
**never checked on a real AMD machine**. A generation is only called "validated" with a real report, archived in
`tests/Shared/validations` (see [CONTRIBUTING.md](../CONTRIBUTING.md) §1). For a card, that is
`GpuTuningCoverage.ValideesEnVie`, and a guard refuses a validated architecture without its report; the processor
readings will follow the same rule, generation by generation.

Each line below says what the path does, its state, how to test it and what the report must show. Testing uses the zips
of the [Release](https://github.com/ablanchard-dev/drdxt/releases): `DrDXT-Validation-AMD.zip` (`VALIDER.cmd`, which
sets, reads back, restores and reads back each setting) and `DrDXT-Rapport-Lecture-Seule.zip` (`DrDXT-Test.exe`, which
changes nothing). The report goes in an "AMD validation" issue.

## The machines we lack

One generation at a time is enough; each one counts:

- Processor: Zen 2 (Ryzen 3000), Zen 3 (Ryzen 5000), Zen 4 (Ryzen 7000), Zen 5 (Ryzen 9000), and a two-block X3D
  (7950X3D, 9950X3D) for cache routing.
- Card: RDNA 2 (RX 6000), RDNA 3 (RX 7000), RDNA 4 (RX 9000).

## The protocol, for each path

1. Close all games. Note the processor, the card, the AMD driver version and the DrDXT version.
2. Before: read the value in the reference tool (AMD Software Adrenalin for the card, HWiNFO for sensors, Ryzen Master
   for the processor, ZenTimings for memory).
3. Run the zip. For a write, `VALIDER.cmd`; for a reading, `DrDXT-Test.exe`.
4. After: read the value again in the reference tool; it must be the one from before.
5. Do it once more with HWiNFO open during the test (two tools reading at the same time must not corrupt anything).
6. Do it once more without administrator rights (decline the Windows prompt): every reading that needs them must say
   "not read", and nothing must crash.

For a write, the report must carry the line `rendu        : oui` without "PAS REMIS D'ORIGINE"; for a reading, the value DrDXT read next
to the one from the reference tool.

## Writes on the card (AMD driver, ADLX)

| Path | What it does | State | Test | The report shows |
|---|---|---|---|---|
| Power limit (`gpu.power_limit`) | lowers the card's power limit through the driver | not offered on AMD: the unit of the ADLX range (GetPowerLimitRange), % of TDP or offset, is not read on a real card | `VALIDER.cmd` | nothing is written: the limit is only read |
| Anti-Lag (`latency.render_queue_cap`) | turns Anti-Lag on to shorten the render queue | not validated on real AMD | `VALIDER.cmd` | Anti-Lag set, read back, restored, read back; Adrenalin agrees |
| Driver Vsync and FRTC cap (`latency.vrr_vsync_cap`) | on a variable refresh display, Vsync "always on" and a frame cap below the display's refresh rate | not validated on real AMD | `VALIDER.cmd`, variable refresh display plugged into the card | both settings set then restored to their original value; Adrenalin agrees |
| Tested maximum frequency (`gpu.undervolt`, Expert mode) | tries a lower maximum frequency, keeps it only if it holds | not validated on real AMD | `VALIDER-FREQUENCE.cmd`, separate from `VALIDER.cmd` (which never writes a frequency) | the original maximum frequency, the one lowered by 50 MHz, then the original read back; Adrenalin agrees before and after |

## Writes on the processor

| Path | What it does | State | Test | The report shows |
|---|---|---|---|---|
| X3D CCD routing (`fixit.x3d_ccd`) | starts AMD's 3D V-Cache optimizer and automatic Game Mode, so the game runs on the cache block | not validated on real AMD | not in the kit yet | to come |
| Boost mode (`fixit.mode_boost`), power plan (`fixit.power_plan`) | Windows settings, also on a Ryzen; the plan is not offered on a two-block X3D | not validated on real AMD | `VALIDER.cmd` | set, read back, restored, read back |

## Card readings (ADLX)

| Path | What it does | State | Test | The report shows |
|---|---|---|---|---|
| Driver settings | reads SAM, Anti-Lag, Chill, FRTC, Vsync and the card temperature | not validated on real AMD | `DrDXT-Test.exe --lire`, in a command prompt opened in the zip's folder | each value read by DrDXT, and the same in Adrenalin (HWiNFO for the temperature) |

## Processor readings

| Path | What it does | State | Test | The report shows |
|---|---|---|---|---|
| Generation and fTPM | recognises the generation by CPUID; for fTPM-related stutter (AMD article PA-410), calls a BIOS "to check" from its date | not validated on real AMD | `DrDXT-Test.exe` ("Docteur : tous les constats" step) | the generation read, and the exact processor (Ryzen Master) |
| Named AMD BIOS settings | reads the BIOS settings (including Curve Optimizer and PBO scale) and what differs from factory values | not validated on real AMD | not in the kit yet | to come: the settings read, and the same in the BIOS |
| Temperature (Tctl and per CCD) | thermal registers, through PawnIO's AMD modules (embedded, read-only) | not validated on real AMD | `DrDXT-Test.exe --lire`, PawnIO installed | the `Tctl` and `CCD` lines within 1 °C of HWiNFO |
| Hardware cause of restarts | the processor's restart register (S5_RESET_STATUS), read without clearing it | not validated on real AMD | `DrDXT-Test.exe --lire`, PawnIO installed | the `S5 (reset)` line after a clean shutdown, then after a long press on the power button (bit 1) |
| Applied memory timings | the timings the memory controller really applies, channel by channel | not validated on real AMD | `DrDXT-Test.exe --lire`, PawnIO installed | the raw `Timings channel` registers, and a ZenTimings screenshot of the same session |
| PPT, TDC, EDC and thermal limits | the processor's power table, in game, through PawnIO's RyzenSMU module (embedded, read-only) | not validated on real AMD | not in the kit yet | values and limits, and the same in HWiNFO or Ryzen Master |
| Corrected hardware errors (MCA) | the errors the processor corrected | pending the module | to come | the errors read, and the same in Event Viewer (WHEA) |

Per-core Curve Optimizer is not read from the processor: it would take a command outside the closed reading list. It is
still read from the BIOS.
