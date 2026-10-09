# PresentMon spine — manual in-game smoke test

The `PresentMonApi2` `[DllImport]` layer is the one boundary the unit tests can't cover
(they fake `IPresentMonApi`). This manual smoke validates the **real** P/Invoke marshaling
end-to-end: open session → track process → register frame query → consume frames → parse the
blob at the service-reported `dataOffset` → frametimes/stats.

## Prerequisites
1. The PresentMon service must be running (it owns the ETW session). Start it **elevated**:
   `third_party/presentmon/PresentMonService.exe` (run as admin). It creates the default control pipe the API connects to.
2. A game (or any presenting process) running and rendering.

## Run
The harness uses DrDXT's OWN adapter via DI (`AddDrDxtMeasurement` → `IBenchmarkRunner`),
**non-elevated** — proving the product model (service elevated once, client as normal user):

```
dotnet run --project tools/smoke -- <game-pid> <seconds>
```
(`tools/smoke` is not part of `DrDXT.sln`; build it on demand. If `PresentMonAPI2.dll` isn't next
to the harness output, copy it from `third_party/presentmon/`.)

## Expected
`RESULT OK: frames=<N>0 avgFps=... p1Low=... p01Low=... moE=±... runs=1` with N>0 and plausible FPS.
- `RESULT FAIL: MEAS.PMON_UNAVAIL` → the service isn't running.
- `RESULT FAIL: MEAS.ANTICHEAT_BLOCKED` → process alive but 0 frames (anti-cheat blocks ETW).

## Last validated
**2026-06-22 — Warzone (`cod.exe`, Ricochet anti-cheat), in a live ranked session, RTX 4060:**
```
RESULT OK: frames=710 avgFps=139.3 p1Low=107.4 p01Low=93.6 moE=±0.99 runs=1
```
Captured by the **non-elevated** DrDXT `BenchmarkRunner` against the real `PresentMonAPI2.dll`
with the elevated `PresentMonService` running. Confirms: P/Invoke marshaling correct, Ricochet
does not block ETW capture, and the elevated-service / non-elevated-client model works.
