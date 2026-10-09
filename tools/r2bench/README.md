# r2bench — deterministic GPU bench (R2)

A controlled, repeatable GPU workload that replaces noisy live-gameplay A/B. It runs
the **same** deterministic compute workload every time, so the only thing that moves
the score is the GPU clock/throughput. Use it to compare stock vs. an applied clock
offset **cleanly**.

It is also the real GPU **stability probe**: it verifies the GPU's output against a
known-answer checksum (a mismatch = silent corruption = unstable, because "no TDR
does not mean stable"), and detects device-lost / TDR.

## What it is / isn't

- **READ / COMPUTE ONLY.** It does **not** call NVAPI and does **not** write any GPU
  tuning. **No elevation needed** — run it as a normal user.
- Applying the clock offset stays the **helper's job**, a **separate UAC step**
  (`tools/r2apply` / the elevated helper). r2bench never touches GPU tuning.
- Lives **outside** `DrDXT.sln` (like `tools/smoke`, `tools/r2probe`), so it never
  affects the product build or tests.

## Run

```
dotnet run --project tools/r2bench -- [gpuIndex] [seconds]
```

- `gpuIndex` (default `0`) — informational.
- `seconds` (default `5`) — bounded load duration.

Example:

```
dotnet run -c Release --project tools/r2bench -- 0 5
```

It prints, repeatably:

- `stability` — Stable / Unstable / Inconclusive
- `checksum-ok` — did the GPU's deterministic output match the known answer
- `checksum` — the 64-bit checksum (same every healthy run)
- `iterations done` and `gpu elapsed`
- **`SCORE`** — element-iterations/sec, the comparable number for the A/B

Exit code: `0` stable, `1` unstable, `2` inconclusive / could-not-run.

## The clean A/B (replaces live-match noise)

1. **Stock:** run `r2bench`, note the `SCORE` (and confirm `STABLE` + `checksum-ok`).
2. **Apply the offset** via the elevated helper (separate UAC step) — e.g.
   `tools/r2apply` applies a `+offset` MHz core-clock change.
3. **Re-run** `r2bench`, note the new `SCORE`.
4. **Compare.** Because the workload is identical run-to-run, a higher `SCORE` is a
   real throughput gain from the offset (not scene noise). If the run comes back
   `UNSTABLE` (TDR or checksum mismatch), the offset is bad — revert it.

Because the workload is fixed and the checksum is a known answer, the score and the
stability verdict are both deterministic — no live-gameplay variance.
