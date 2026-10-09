# r2leasesmoke — LIVE proof of the R2 lease watchdog (dead-man's-switch)

This harness proves **end-to-end** that a **held GPU clock offset**, applied
**through the running elevated helper**, is **auto-reverted by the helper's
background watchdog** when the controller dies **without renewing** the lease.

It is **not** part of `DrDXT.sln`.

What `r2leasesmoke.exe` does:

1. read the current core-clock offset on GPU 0 (baseline; expect `0 MHz`) for the printout
2. apply a **conservative held offset of `-15 MHz`** on GPU 0 via `ApplyNvApiClockOffsetOp`
   **through the helper** (the helper routes it to the **real** `NvApiTuningActions` write)
3. establish a lease (`EstablishLeaseOp`) whose **revert op = `ApplyNvApiClockOffsetOp(gpu 0, 0 MHz)`**
   (back to stock), with **`leaseSeconds = 8`**
4. print what was applied + the lease id, then **EXIT IMMEDIATELY** — **no heartbeat, no Complete**

Because the process exits without renewing, it simulates a controller that died.
The helper's background monitor ticks every **~5 s** and, once the lease's 8 s
expire, runs the recorded revert → the offset returns to **0 MHz** on its own.

> This branch (`feat/r2-lease-watchdog`, off `5fa9b29`) has the **real** NVAPI
> clock-offset write wired: `PrivilegedOpExecutor` routes `ApplyNvApiClockOffsetOp`
> to `NvApiTuningActions.SetCoreClockOffset` (commits `fb4a8c0` / `11b6b41`), **not**
> a deferred fail-clean seam. So the offset really moves.

---

## Safety / scope

- Conservative `-15 MHz` underclock, well inside `GpuSafeBounds` — observable, reversible.
- Requires an **NVIDIA GPU** and the **running elevated helper** (the write + the
  watchdog both live in the elevated helper). Reads need no elevation; the apply does.
- If the lease somehow fails to register, the smoke undoes the offset itself so the
  GPU is never left held by accident.

---

## Build (no elevation)

```powershell
dotnet build .\src\DrDxt.ElevatedHelper\DrDxt.ElevatedHelper.csproj -c Debug
dotnet build .\tools\r2leasesmoke\r2leasesmoke.csproj -c Debug
dotnet build .\tools\r2probe\r2probe.csproj -c Debug
```

## Run the LIVE proof (PowerShell, as the SAME user)

The helper authorizes **only** the caller image path it is launched with
(`--app-path`), and the helper + smoke must share the **same** session key.

```powershell
# 1) one shared per-session HMAC key (>= 32 bytes, base64), for BOTH processes
$key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
$env:DRDXT_HELPER_SESSION_KEY = $key

# 2) resolve the built exes
$helper    = ".\src\DrDxt.ElevatedHelper\bin\Debug\net8.0-windows\DrDxt.ElevatedHelper.exe"
$smoke     = ".\tools\r2leasesmoke\bin\Debug\net8.0-windows\r2leasesmoke.exe"
$probe     = ".\tools\r2probe\bin\Debug\net8.0-windows\r2probe.exe"
$smokeFull = (Resolve-Path $smoke).Path

# (a) launch the helper ELEVATED (UAC prompt), trusting the smoke exe as the caller.
#     It reads the key from the inherited env var AND the --session-key arg.
Start-Process -Verb RunAs -FilePath $helper `
  -ArgumentList "--session-key=$key", "--app-path=$smokeFull"

# (b) in the SAME shell (same $env:DRDXT_HELPER_SESSION_KEY), run the smoke.
#     It applies the held -15 MHz offset, arms the 8 s lease, and EXITS at once.
& $smoke

# (c) wait ~15 s so the lease (8 s) expires AND a watchdog tick (~5 s) fires
Start-Sleep -Seconds 15

# (d) confirm the offset is back to 0 MHz (read-only probe; no elevation needed)
& $probe

# (e) stop the elevated helper when done
taskkill /F /IM DrDxt.ElevatedHelper.exe
```

### What PASS looks like

`r2leasesmoke.exe` tail (step b):

```
  [PASS] Step 1 baseline core-clock offset(GPU 0) = 0 MHz (expected 0)
  [PASS] Step 2 applied HELD offset -15 MHz on GPU 0 THROUGH the helper
        read-back after apply = -15 MHz (expected -15)
  [PASS] Step 3 established lease id='r2leasesmoke:gpu0:<guid>' (revert→0 MHz, leaseSeconds=8)

  Applied: HELD core-clock offset -15 MHz on GPU 0, through the elevated helper.
  Lease  : 'r2leasesmoke:gpu0:<guid>', leaseSeconds=8, revert op = back to 0 MHz.
  Exiting WITHOUT renew/complete — the helper watchdog should auto-revert to 0
  within ~(leaseSeconds + monitor tick) = ~8 + ~5s. Wait ~15s, then run
  tools/r2probe and confirm GetCoreClockOffsetMhz(0) is back to 0 MHz.

=== r2leasesmoke: lease armed, exiting now (NO heartbeat, NO Complete) ===
```

`r2probe.exe` tail (step d) — **this is the proof**:

```
  GetCoreClockOffsetMhz(0)  = OK  -> current P0 graphics offset = 0 MHz
```

If the probe reports **`0 MHz` after the smoke exited without reverting**, the
**watchdog auto-reverted the held offset** → validation A **PASSES**.

> Optional tighter proof: run `& $probe` IMMEDIATELY after `& $smoke` (before the
> sleep) and you should see `-15 MHz` (offset still held); then after the 15 s sleep
> it should read `0 MHz` (watchdog fired). That shows the revert was the watchdog,
> not the apply.

## Troubleshooting

- `ELEVATION_DENIED` on apply/establish → the helper's `--app-path` does not match
  the **full path** of `r2leasesmoke.exe`. Re-launch the helper with the exact
  `$smokeFull` path printed by `(Resolve-Path $smoke).Path`.
- `IO_FAILURE` ("could not reach the elevated helper") → the elevated helper is not
  running (UAC declined / it already exited / wrong session).
- `HELPER.SIGNATURE_INVALID` → the helper and smoke do not share the same session
  key; set `$env:DRDXT_HELPER_SESSION_KEY` for both in the **same** shell.
- Step 2 `HELPER.OP_FAILED` (e.g. "NVIDIA NVAPI unavailable" / "SetPstates20
  unavailable") → not an NVIDIA box, or the private NVAPI write path is unavailable
  on this driver. The watchdog logic is still correct, but this smoke needs a real
  NVIDIA GPU to move the clock.
- Probe still shows `-15 MHz` after ~15 s → wait a few more seconds (one more tick),
  then re-run the probe. If it never reverts, capture the helper console output and
  the `helper.journal.log` under `%ProgramData%\DrDxt\` (before 25/09/2026:
  `%LOCALAPPDATA%\DrDXT\helper-diagnostics\`).
