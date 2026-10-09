# m2smoke — LIVE proof of the M2 SAFE apply→revert pipeline

This harness drives the **running elevated helper** over the signed, allowlisted,
typed IPC and performs a **real** power-plan switch + revert — the one part the
unit tests deliberately cannot cover (the native boundary). It is **not** part of
`DrDXT.sln`.

What it does, printing PASS/FAIL per step:

1. read the current active power plan
2. `EnsureRestorePoint` → `CreateRestorePointOp` (through the helper)
3. apply `SetActivePowerPlanOp(High Performance)` (or Balanced if already on High Perf)
4. read back & assert the active plan **changed**
5. revert to the **original** plan
6. read back & assert the active plan is **restored**

Exit code `0` = all PASS.

> NVAPI + ADLX undervolt / power-limit writes are now WIRED (R2/T3). Only the
> multi-point GPU fan-curve stays **deferred** (E4 fan-write is a dedicated future
> task): the executor refuses it cleanly (`HELPER.OP_FAILED`, "GPU fan-curve control
> is not yet available — no fan write is performed (E4)"). This smoke only proves the
> SAFE power-plan path.

---

## Build

```powershell
dotnet build .\src\DrDxt.ElevatedHelper\DrDxt.ElevatedHelper.csproj -c Debug
dotnet build .\tools\m2smoke\m2smoke.csproj -c Debug
```

## Run the LIVE proof (PowerShell, as the same user)

The helper authorizes **only** the caller image path it is launched with
(`--app-path`), and the helper + smoke must share the **same** session key.

```powershell
# 1) one shared per-session HMAC key (>= 32 bytes, base64), for BOTH processes
$key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
$env:DRDXT_HELPER_SESSION_KEY = $key

# 2) resolve the built exes
$helper = ".\src\DrDxt.ElevatedHelper\bin\Debug\net8.0-windows\DrDxt.ElevatedHelper.exe"
$smoke  = ".\tools\m2smoke\bin\Debug\net8.0-windows\m2smoke.exe"
$smokeFull = (Resolve-Path $smoke).Path

# 3) launch the helper ELEVATED (UAC prompt), trusting the smoke exe as the caller.
#    It reads the key from the inherited env var AND/OR the --session-key arg.
Start-Process -Verb RunAs -FilePath $helper `
  -ArgumentList "--session-key=$key", "--app-path=$smokeFull"

# 4) in the SAME shell (same $env:DRDXT_HELPER_SESSION_KEY), run the smoke
& $smoke
```

Expected tail:

```
  [PASS] Step 1 read active power plan = Balanced
  [PASS] Step 2 CreateRestorePointOp (restore point created)
  [PASS] Step 3 apply SetActivePowerPlanOp(High Performance)
  [PASS] Step 4 active plan changed to High Performance
  [PASS] Step 5 revert SetActivePowerPlanOp(Balanced)
  [PASS] Step 6 active plan restored to Balanced

=== OVERALL: PASS ===
```

## Notes

- **System Restore must be ON** for Step 2 to create a point. If it is off, Step 2
  prints `[WARN]` and the proof continues (the apply/revert wiring is still proven).
- If you see `ELEVATION_DENIED`, the helper's `--app-path` does not match the smoke
  exe path. Re-launch the helper with the exact `m2smoke.exe` full path.
- If you see `IO_FAILURE` ("could not reach the elevated helper"), the elevated
  helper is not running (or the UAC prompt was declined).
- If you see `HELPER.SIGNATURE_INVALID`, the helper and the smoke do not share the
  same session key — set `$env:DRDXT_HELPER_SESSION_KEY` for both.
