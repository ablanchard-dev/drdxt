# helperhandshake — M5 helper-launch + §9.13 handshake live proof

Manual LIVE smoke for the M5 elevation foundation. **Not part of `DrDXT.sln`.**

It proves, end to end and with **no pre-shared key**:

1. locate `DrDxt.ElevatedHelper.exe` next to the tool (the ProjectReference copies it);
2. launch the helper **elevated** in **handshake mode** (one interactive UAC, via
   `HelperLauncher` + `RunAsProcessLauncher`) — the helper is told to trust *this*
   exe as the caller (`--app-path=<this exe>`); the session key is **not** passed;
3. connect and run the **real §9.13 handshake** (`HandshakingElevatedClient`):
   the helper authenticates the caller, then mints + hands over an **ephemeral
   per-session key**;
4. execute a harmless allowlisted op (`PingOp`) through the negotiated, signed,
   allowlisted typed IPC.

Each step prints `PASS`/`FAIL`; exit code `0` = all PASS.

## Run

```powershell
# 1) Build (copies DrDxt.ElevatedHelper.exe next to the tool)
dotnet build tools/helperhandshake/helperhandshake.csproj -c Debug

# 2) Run — APPROVE the single UAC prompt that launches the elevated helper
tools\helperhandshake\bin\Debug\net8.0-windows\helperhandshake.exe
```

## Notes

- The real `runas` spawn (interactive UAC) is the untested unit edge; this tool is
  how it is exercised live. The decision logic around it (`HelperLauncher`) and the
  handshake state machine are unit-tested in `DrDxt.ElevatedHelper.Tests`.
- The elevated helper keeps running after the proof; close its window or end the
  process to stop it.
- Caller auth here is the **dev-grade image-path** check the helper wires for
  `--app-path`. The real Authenticode (signed release builds) is the documented
  M5 TODO; this tool runs against the unsigned dev build.
