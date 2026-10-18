# Changes

Version française, avec les versions publiées : [CHANGELOG.md](CHANGELOG.md).

## Unreleased

For the player:

### Machine safety
- A setting DrDXT applies is noted before it is written: if DrDXT stops before keeping its original value, that value
  is restored at the next action as administrator, or at Windows startup if the service is installed, never during a
  match.
- No Windows prompt opens without an action of yours; the background recording of your matches runs without
  administrator rights.
- A write that DrDXT cannot note in advance is refused, rather than made without a safety net.
- Programs slowed down during a match always get their priority back, even when the option is turned off and on
  again, and when DrDXT closes.
- DrDXT's files are never written through a link placed in advance, and a rewrite goes through a new file, never in
  place.
- Update: the new version is announced by a signed file, the download only starts on your click, and never from an
  app started as administrator.
- Installer: an update stops DrDXT's programs cleanly and removes old copies of PresentMon.
- Uninstall: the settings you kept go back to their original value and only DrDXT's files are removed; an NVIDIA card
  goes back to automatic fan and a 0 MHz offset, even an offset set by another tool; the AMD undervolt is not restored
  yet, and that is said.
- Page file: never turned off by the uninstaller; moved to an SSD, it goes back to its place, except sizes set by
  hand, which the report names.
- Fixed fan speed: it hands control back to the card's curve from 80 °C, and within two minutes if DrDXT stops
  answering.
- AMD card: the factory reset only runs on your click.
- Processor power limit and undervolt: bounds checked again at the moment of writing; no higher limit while no match
  temperature has been recorded.
- Card tuning is refused when another tool has already set an offset, or when that offset could not be read.
- Upload cap during a match: only for a recognised game, and forgotten at restart.
- An antivirus or a parental control is never turned off at startup.
- Windows credentials only go to a proxy you configured, never to a proxy discovered on the network.
- The stability, driver, Defender and BitLocker readers cannot write anything.
- Incident witness folder hardened; development update signing key rotated.

### Honest messages
- A failure is said in a plain sentence, never with the technical message, and its reason claims nothing about the
  machine that it does not know.
- When the administrator side stops answering after a request, the app says "may or may not be set", no longer
  "failed".
- What could not be read stays "not read", never a guessed value: display refresh rate, VRR, thermal cap without a
  match temperature.
- The other way round, a setting never written reads as the Windows or driver default, no longer as unreadable: Game
  Mode, NVIDIA settings.
- A setting already at its value is said already set; a refused rollback is said half set; a failed registration or
  removal says so.
- PC latency is said estimated; a single undervolt step that held promises no margin; Expert mode on the card gives
  the real length of its test instead of "Stable".
- Numbers and dates follow the app's language; English texts use English quotation marks.
- Expert information with no problem is folded into "Technical details" under each page; the "recommendation" label
  is gone.
- The README says exactly what remains after uninstalling, and what was checked on a real machine.
- "Send my report" says the ticket opens empty.
- "What's new" shown once after a DrDXT update; a one-click support bundle, shown before it is written.

### Measurement
- Every-other-game trial in A-B-B-A blocks, with a dry run before any verdict; a latency setting is judged on the
  latency of its matches.
- Display latency only counts the moments played; menus no longer count.
- Frame time curve of each measurement, with its median; spikes above the 99th percentile are marked.
- Two different ways of computing the 1 % low are no longer compared; "not comparable" no longer wrongly points at
  the earlier match.
- The cost of an overlay is judged on three matches on each side, like the other settings.
- Matches: share of composed frames, PC latency, game memory held outside the card; summary of OBS sessions.
- Test of your line under load, on a click, against a server named in advance, capped and cancellable.
- The scan gauge follows the real length of the tests; Windows logs are read only once per scan.

### Diagnosis
- Stability: blue screens counted from three sources (stop code, Windows report, dumps), hard shutdowns from the
  Kernel-Power 41 event only, hardware errors with the core or PCIe link that raised them, and what Windows noted just
  before a crash kept with nothing to run.
- NVMe SSD health read from the drive itself; SSD firmware that its maker has since fixed, flagged with its bulletin.
- Windows memory test: a shutdown during the test counts as a failure, and the test never takes more than half of the
  free memory.
- Curve Optimizer and memory profile (XMP/EXPO) advice held back on any unstable machine; no more Curve Optimizer
  offered to a 5000-series X3D.
- Drivers: full inventory, driver floor that each game sets, blocked install judged the way Windows does, missing
  32-bit graphics driver (launcher that stays black), RTCore driver flagged.
- Full memory judged on measured paging, not on free memory alone.
- Processor: time spent in C1, C2 and C3, cost of the high-resolution clock, core most used by drivers, Spectre and
  Meltdown protections said without ever offering to turn them off.
- Ryzen: generation read by CPUID; Curve Optimizer and PBO scale read from the BIOS; applied timings and PPT, TDC, EDC
  and thermal limits ready, waiting for the AMD reading module.
- BIOS: the settings no longer at their factory value; those of an AMD board named, board by board.
- Published regressions: DrDXT warns when an update, a driver or your processor is listed; its tables arrive signed,
  without changing the app version.
- Settings another tool may have left in the registry, with their date.
- Resizable BAR proven by the memory range allocated to each card; on an X3D, badly parked cores of the cache block
  are seen.
- Input: each mouse, keyboard and controller with its role and link, software layers in between, rate requested from
  the bus, reports actually sent.
- Display: hardware overlays turned off, duplicated screens at two refresh rates, encoding and colour depth screen by
  screen.
- Network: forced speed or duplex, programs using the upload, NAT type read from the router (read only).
- Timeline: games updated and display drivers changed since the previous scan.
- Before playing: last real restart, video memory already taken, virtual audio, encrypted drive.

### Upkeep
- Upkeep pause: a switch in the app suspends restoring settings at startup; a doubtful pause file counts as a pause,
  and the uninstaller removes it.
- The startup service waits for the end of a match for its daily pass.
- After a Windows update, everything DrDXT set is read back and the result is said.
- "Check DrDXT on my PC" can be followed live, stops between two settings after confirmation, reads each setting back
  after setting it, and does not touch a setting held by the every-other-game trial.
- Tracked settings: a failed read no longer loses the history, and the app and the background recording no longer
  get in each other's way.
- One app per session: a second launch shows the open window.
