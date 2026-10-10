# DrDXT

Version française : [README.md](README.md). The app speaks English and French. For players and contributors,
[CONTRIBUTING.en.md](CONTRIBUTING.en.md), [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md) are in English; the
technical documents under `docs/` are in French.

A PC gaming optimizer for Windows. Free, open source, and honest: it promises nothing it has not measured.

DrDXT reads your machine, tells you what is costing you frames, and only applies settings it knows how to undo. Every
setting is judged on your real matches: kept if it brings something, reverted otherwise. If your machine is already
well set up, it tells you that too.

## What it does

- **A scan** reads your processor, graphics card, memory, storage, Windows, network and BIOS. Nothing is written at
  this point.
- **A doctor per domain** explains each point: what was read, what is wrong, what DrDXT can do, and what has to be done
  by hand (BIOS, cables, in-game settings), step by step.
- **Optimize** applies the useful settings one by one, after a restore point. **Try it every other game** compares one setting
  across your matches, every other match, and decides after six.
- **Your matches** are recorded in the background: frames per second, the slowest 1 %, what was the bottleneck, which
  programs were open alongside. This is the only proof DrDXT accepts.
- **Expert mode** opens the graphics card's clock offset and fan speed, with your consent, with a stability test and
  automatic rollback.
- **Check DrDXT on my PC** applies each setting, reads it back, reverts it and reads it again, then writes a report
  with nothing that identifies you. This is how your hardware enters what DrDXT can prove.

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

The most useful help does not require writing code:

- **Check DrDXT on your PC**, then drop the report into a "Verification report" issue.
- **Without installing DrDXT**: the zips in the [Release](https://github.com/ablanchard-dev/drdxt/releases) read your
  PC, or run the same verification, and write the same report.
- **Report a bug**, or propose code: [CONTRIBUTING.en.md](CONTRIBUTING.en.md). A security issue is reported in
  private: [SECURITY.md](SECURITY.md).

## What DrDXT writes on your machine

Every setting goes through a separate elevated program, the helper, which refuses anything not on its list. Every
write first reads the original value and knows how to restore it. The full list:

- **Registry, exact values**, never a whole branch: `HwSchMode` (hardware-accelerated GPU scheduling), `PagingFiles`
  (disabled page file, restored), `AllowAutoGameMode` and `AutoGameModeEnabled` (Game Mode), `HistoricalCaptureEnabled`
  (Game Bar background recording), `DirectXUserGlobalSettings` (VRR and presentation model), `MouseSpeed`,
  `MouseThreshold1` and `MouseThreshold2` (pointer acceleration). Two values are no longer applied, only reverted if an
  older version wrote them: `LowLatencyMode` and `CppcEnable`.
- **NVIDIA driver**, on the game's profile only: render queue, vertical sync, frame limiter, power mode.
- **Power plan**: a known Windows plan, and processor boost within that plan.
- **One restore point** before applying; System Restore is turned on for the system drive if it was off. At most two
  DrDXT points, reused within 24 hours.
- **Two services**: PresentMon, which measures frames, and the AMD 3D V-Cache service.
- **Graphics card**, within bounds: the power limit, tried in-game and kept only with a measured gain; in Expert mode,
  with your consent, the clock offset and a fixed fan speed, reverted on their own if DrDXT stops.
- **Two QoS rules** during a match, on request, to cap upload; removed when the match ends.
- **Windows boot settings**, five exact elements, on request, to remove a setting left by a script.
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

6,784 tests, 0 failures, measured on 10/10/2026 across 17 projects, excluding tests marked `Machine` (they read the
real hardware). The number is checked by `tools/gates/compte_des_tests.py` against the last run.

The installer is built with `installer/build-installer.ps1`. It also needs **Inno Setup 6** and the Visual Studio C++
tools, to compile natively the launcher that receives elevation.

## License

DrDXT is free software under **GPL-3.0**. The full text is in `LICENSE` and ships with every copy. You may use, copy,
modify and redistribute it, even for money, provided you publish the source code of your version under the same
license. A closed version, or a paid one without source, is not allowed. The name "DrDXT" refers to this project: a
modified version goes by its own name.

Shipped alongside: PresentMon (Intel, MIT license, `PresentMon-NOTICE-MIT.txt`) and QRCoder (MIT license,
`QRCoder-LICENSE-MIT.txt`). NVAPI (NVIDIA) and ADLX (AMD) are not shipped: DrDXT calls the libraries your driver
installed, from their public headers.

## The five invariants

Five rules hold the code, and each comes from a real failure:

- **Proven, never claimed.** A gain is shown only above the noise of the measurement. "No measurable gain" is a valid
  answer, and often the right one.
- **Surgical.** No service disabled, no Windows debloat, no bolted-on script. What requires the BIOS stays a guide.
- **Reversible.** Original value read before any write, rollback tested, restore point.
- **Honest about unavailability.** A setting that does not apply says which one and why.
- **Permanent blacklist.** The optimization myths (Nagle, `NetworkThrottlingIndex`, 8,000 Hz polling, realtime
  priority, C-states disabled wholesale) are refused by a test that breaks if one of them enters the catalog.

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

Where to start: [docs/NON-NUISANCE.md](docs/NON-NUISANCE.md), [docs/SECURITE.md](docs/SECURITE.md), then
[docs/INSTRUMENTS.md](docs/INSTRUMENTS.md) and [docs/OU-VIT-LE-GAIN.md](docs/OU-VIT-LE-GAIN.md) for what each
measurement can prove. Working conventions are in [CONTRIBUTING.en.md](CONTRIBUTING.en.md).
