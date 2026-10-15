<p align="center">
  <img src="docs/images/banniere.png" alt="DrDXT, the honest PC optimizer for gamers" width="100%">
</p>

<p align="center">
  <a href="https://github.com/ablanchard-dev/drdxt/releases"><img alt="Download the Windows installer" src="https://img.shields.io/badge/download-Windows%20installer-3DF5FF?style=flat-square&labelColor=06070A"></a>
  <a href="LICENSE"><img alt="License GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-8B5CFF?style=flat-square&labelColor=06070A"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-FF4FD8?style=flat-square&labelColor=06070A">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-FFB547?style=flat-square&labelColor=06070A">
</p>

<p align="center">
  <a href="https://github.com/ablanchard-dev/drdxt/releases"><b>Download</b></a> ·
  <a href="#what-it-does">What it does</a> ·
  <a href="#contributing">Contributing</a> ·
  <a href="docs/COUVERTURE.md">What is proven</a> ·
  <a href="README.fr.md">Français</a>
</p>

A PC gaming optimizer for Windows. Free, open source, and honest: it promises nothing it has not measured.

DrDXT reads your machine, tells you what is costing you frames, and only applies settings it knows how to undo. Every
setting is judged on your real matches: kept if it brings something, reverted otherwise. If your machine is already
well set up, it tells you that too.

<p align="center">
  <img src="docs/images/accueil-en.png" alt="DrDXT home screen: the machine read domain by domain around the chrome sphere" width="100%">
</p>
<p align="center">
  <img src="docs/images/memoire-en.png" alt="The memory domain: what was measured, what was found, what DrDXT can do" width="49%">
  <img src="docs/images/graphique-en.png" alt="The graphics card domain, with the expert mode folded" width="49%">
</p>

The app speaks English and French. [CONTRIBUTING.md](CONTRIBUTING.md), [PRIVACY.md](PRIVACY.md) and
[SECURITY.md](SECURITY.md) are in English; the technical documents under `docs/` are in French.

## What it does

- **A scan** reads your processor, graphics card, memory, storage, Windows, network and BIOS. Nothing is written at
  this point.
- **A doctor per domain** explains each point: what was read, what is wrong, what DrDXT can do, and what has to be done
  by hand (BIOS, cables, in-game settings). When a point has a fix, it reads apart under **How to fix**, in numbered
  steps that name what was read on your machine: the app, the value, the path.
- **Optimize** applies the useful settings one by one, after a restore point. **Try it every other game** compares one setting
  across your matches, every other match, and decides after six.
- **Your matches** are recorded in the background: frames per second, the slowest 1 %, what was the bottleneck, which
  programs were open alongside. This is the only proof DrDXT accepts. The game is found on its own, full screen or
  borderless, and the most played ones even windowed; launchers, Discord and maximized desktop apps are left aside.
- **Your machine's history** puts matches, shutdowns or errors, and changes on one timeline, with each match's frames per second
  under the game's name.
- **Unreal Engine 5 games**: DrDXT reads the global illumination and reflections settings (Lumen) and, from what
  slowed your last matches of that game, says whether lowering them is worth trying. It is advice: the setting is
  changed in the game, by you.
- **Expert mode** opens the graphics card's clock offset and fan speed, with your consent, with a stability test and
  automatic rollback.
- **Find my undervolt** (Intel) lowers the processor voltage step by step, with a load test at each step and an
  automatic return to the original value. The stable value is then yours to write in the BIOS, guided step by step.
- **Check DrDXT on my PC** applies each setting, reads it back, reverts it and reads it again, then writes a report
  with nothing that identifies you. This is how your hardware enters what DrDXT can prove.

## The five invariants

Five rules hold the code, and each comes from a real failure:

- **Proven, never claimed.** A gain is shown only above the noise of the measurement. "No measurable gain" is a valid
  answer, and often the right one.
- **Surgical.** No service disabled, no Windows debloat, no bolted-on script. What requires the BIOS stays a guide.
- **Reversible.** Original value read before any write, rollback tested, restore point.
- **Honest about unavailability.** A setting that does not apply says which one and why.
- **Permanent blacklist.** The optimization myths (Nagle, `NetworkThrottlingIndex`, 8,000 Hz polling, realtime
  priority, C-states disabled wholesale) are refused by a test that breaks if one of them enters the catalog.

## Where the project stands

Version 0.9, usable today. The scan, the doctor, match recording, Optimize, the every-other-game trial and the rollback
of every setting run on real PCs, and every setting was applied, read back and restored on a real machine before
being offered.

Players have already used a version derived from part of DrDXT, prepared by someone close to the project. Some report
a latency steady around 10 ms, others 30 to 50 more frames per second. These figures come from them: DrDXT has not
measured them itself yet, and what that version does on top is not known yet. That is exactly what the
every-other-game trial and the verification reports are for, PC by PC.

Still to prove: a gain measured by DrDXT over matches, and AMD cards, Intel Arc, dual-GPU laptops and handhelds,
covered in the code but tested only on reconstructed machines. [docs/COUVERTURE.md](docs/COUVERTURE.md) says, setting
by setting, what is proven, what is still to prove, and what is absent from your hardware.

## Install

1. Download `DrDXT-Setup-NON-SIGNE.exe` from the latest [Release](https://github.com/ablanchard-dev/drdxt/releases).
   Its SHA-256 is on the page; `Get-FileHash` in PowerShell recomputes it.
2. The installer is not signed yet: Windows shows "Windows protected your PC". Click "More info", then "Run anyway".
3. Two checkboxes: a Desktop shortcut, and the `DrDxtReconciler` service, unchecked by default, which reapplies at
   Windows startup the settings you kept.
4. On first launch, "Run scan" reads the machine without writing anything. Nothing is applied before you click
   "Optimize" or "Try it every other game", and every setting can be undone from "What DrDXT set".
5. Uninstall: Settings, Apps, DrDXT. The uninstaller first reverts the settings it knows about, then removes the files.
   `%LOCALAPPDATA%\DrDXT` (your records) and `%ProgramData%\DrDxt` (its reports) remain; delete them by hand to start
   from scratch.

Windows 10 or 11, 64-bit, on an **administrator account**: it is the one that consents to each setting. On a standard
account, Windows would ask for an administrator's password and the settings would go into that account's profile:
this is not supported. No .NET to install, everything is in the installer.

## Contributing

DrDXT gets better one PC at a time, and most of what it still has to prove is on hardware it has never seen.
The most useful help does not require writing code:

- **Check DrDXT on your PC**, then drop the report into a "Verification report" issue. Each report moves a setting
  from "to prove" to "proven" on that hardware, in [docs/COUVERTURE.md](docs/COUVERTURE.md).
- **Without installing DrDXT**: the zips in the [Release](https://github.com/ablanchard-dev/drdxt/releases) read your
  PC, or run the same verification, and write the same report.
- **Report a bug**, or a sentence in the app that is unclear or wrong in your language.
- **Talk**: questions, bug reports and testers meet on [Discord](https://discord.gg/z6HJWDzfB3).

Writing code? [ARCHITECTURE.md](ARCHITECTURE.md) shows how the pieces fit and how to add a setting from end to end;
[CONTRIBUTING.md](CONTRIBUTING.md) explains how to build, test and propose a change. Every pull
request runs the full test suite in CI. A security issue is reported in private: [SECURITY.md](SECURITY.md).

## What DrDXT writes on your machine

Every setting goes through a separate elevated program, the helper, which refuses anything not on its list. Every
write first reads the original value and knows how to restore it. The full list:

- **Registry, exact values**, never a whole branch: `HwSchMode` (hardware-accelerated GPU scheduling), `PagingFiles`
  (disabled page file restored, or moved to an SSD), `AllowAutoGameMode` and `AutoGameModeEnabled` (Game Mode), `HistoricalCaptureEnabled`
  (Game Bar background recording), `DirectXUserGlobalSettings` (VRR and presentation model), `MouseSpeed`,
  `MouseThreshold1` and `MouseThreshold2` (pointer acceleration). Two values are no longer applied, only reverted if an
  older version wrote them: `LowLatencyMode` and `CppcEnable`.
- **Startup apps**, on request: the Windows on/off switch (`StartupApproved`) of an entry you choose, as Task Manager
  does.
- **NVIDIA driver**, on the game's profile only: render queue, vertical sync, frame limiter, power mode, and on request
  the DLSS override to the latest model.
- **AMD driver**, through ADLX: power limit, Anti-Lag, frame rate target, vertical sync.
- **Power plan**: a known Windows plan, processor boost within that plan, and the Wi-Fi adapter's power saving when the
  game runs over Wi-Fi.
- **Processor** (desktop Intel): the PL1 power limit in its register, raised to what the board allows or capped when the
  processor runs too hot; the BIOS puts its own back at the next boot. On request, an undervolt search, step by step
  under load, with the original voltage restored at the end; the result is yours to set in the BIOS.
- **One restore point** before applying; System Restore is turned on for the system drive if it was off. At most two
  DrDXT points, reused within 24 hours.
- **Two services**: PresentMon, which measures frames, and the AMD 3D V-Cache service.
- **Graphics card**, within bounds: the power limit, tried in-game and kept only with a measured gain; in Expert mode,
  with your consent, the clock offset and a fixed fan speed, reverted on their own if DrDXT stops.
- **Two QoS rules** during a match, on request, to cap upload; removed when the match ends.
- **Windows boot settings**, five exact elements, on request, to remove a setting left by a script.
- **Drivers**, on request: restart a device, remove an old driver package from the driver store and put it back.
- **Windows Memory Diagnostic** at the next boot, on request, and its cancellation.
- **Reboot to BIOS**, on request. The BIOS is read, never written.
- **The signed manifest** of kept settings, in `%ProgramData%\DrDxt`, which the uninstaller reads to revert them.

Never written, whatever is asked: Windows Defender, Windows Update, Windows services, firmware. The details, site by
site, are in [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md); the helper's security and the accepted limits are in
[docs/SECURITE.md](docs/SECURITE.md).

## What leaves the machine

No telemetry, no account. Here is everything DrDXT sends over the network. What it reads and keeps on your machine is
in [PRIVACY.md](PRIVACY.md).

- **The latest graphics driver version**, at each scan, to tell you whether yours is behind: AMD's public page
  (`www.amd.com`) and one fixed request to NVIDIA's public service (`gfwsl.geforce.com`, always the same, queried only
  on a GeForce card). Nothing identifies you.
- **Pings** to your router and to `1.1.1.1`, to measure latency; during a match, also to the game server, to follow the
  route from one match to the next.
- **The update check**, at startup: two small files read from `https://ablanchard-dev.github.io/drdxt`. GitHub sees
  your IP address, as with any web page. To turn it off: the `DRDXT_UPDATE_URL` environment variable set to `off`. An
  update never installs without your click ([docs/MISE-A-JOUR.md](docs/MISE-A-JOUR.md)).
- **Your MSI motherboard's support page**, only if you click "Open your board's page" in the BIOS guide: your browser
  opens `www.msi.com/Motherboard/<model>/support`.
- **A Windows Update search**, only if you click "Check now" under the Windows Update finding: Windows Update asks
  Microsoft's servers what is waiting, as Settings > Windows Update does. Nothing is installed.
- **Your verification report**, only if you click "Send my report": DrDXT opens your browser on a pre-filled GitHub
  issue. Nothing leaves until you submit it.

A crash is written to `%LOCALAPPDATA%\DrDXT\crash.log`, on your machine only. A test fails if an address contacted by
the code is missing from this list.

## Build

Windows 10 or 11 x64 and the **.NET 10** SDK. Nothing else for the application and the tests.

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

**7,164 tests, 0 failures**, measured on 15/10/2026 across 17 projects, excluding tests marked `Machine` (they read the
real hardware). The number is checked by `tools/gates/compte_des_tests.py` against the last run. The command that gives
it back (an English .NET SDK; with a French one, see the French README):

```bash
dotnet test DrDXT.sln -c Release --filter "Category!=Machine" --nologo -v q 2>&1 | grep "Passed!" \
  | sed 's/.*Total[^0-9]*\([0-9][0-9]*\).*/\1/' | awk '{s+=$1; n++} END {print n, "projects |", s, "tests"}'
```

The installer is built with `installer/build-installer.ps1`. It also needs **Inno Setup 6** and the Visual Studio C++
tools, to compile natively the launcher that receives elevation.

## Code map

| Project | Role |
|---|---|
| `DrDxt.Core` | contracts, results, error codes, privileged operations |
| `DrDxt.Detection` | hardware, settings state, network |
| `DrDxt.Measurement` | PresentMon, DXGI fallback, network, memory test |
| `DrDxt.Application` | settings catalog, apply engine, rollback, GPU bounds |
| `DrDxt.ElevatedHelper` | the elevated program that writes, behind its allowlist |
| `DrDxt.Lanceur` | the native executable that receives elevation and relaunches the others with a clean environment |
| `DrDxt.Reconciler*` | signed manifest, reapplication at startup |
| `DrDxt.Bios` | BIOS reading and guide, never a write |
| `DrDxt.GpuNative` | NVAPI, ADLX, NVML |
| `DrDxt.App` | the user interface (WPF) |
| `tools/` | benches and probes outside the solution, including the validation zip (`amd-harness`) |

Where to start: [ARCHITECTURE.md](ARCHITECTURE.md), then [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md), [docs/SECURITE.md](docs/SECURITE.md), then
[docs/INSTRUMENTS.md](docs/INSTRUMENTS.md) and [docs/OU-VIT-LE-GAIN.md](docs/OU-VIT-LE-GAIN.md) for what each
measurement can prove. Working conventions are in [CONTRIBUTING.md](CONTRIBUTING.md).

## License

DrDXT is free software under **GPL-3.0**. The full text is in `LICENSE` and ships with every copy. You may use, copy,
modify and redistribute it, even for money, provided you publish the source code of your version under the same
license. A closed version, or a paid one without source, is not allowed. The name "DrDXT" refers to this project: a
modified version goes by its own name.

Shipped alongside: PresentMon (Intel, MIT license, `PresentMon-NOTICE-MIT.txt`) and QRCoder (MIT license,
`QRCoder-LICENSE-MIT.txt`). NVAPI (NVIDIA) and ADLX (AMD) are not shipped: DrDXT calls the libraries your driver
installed, from their public headers.
