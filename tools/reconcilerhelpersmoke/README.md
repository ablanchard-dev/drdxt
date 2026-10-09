# reconcilerhelpersmoke — M5 reconciler↔helper channel live proof

Manual, human-run boundary proof for the M5 increment (NOT part of `DrDXT.sln`, NOT
run in CI — it **elevates** via UAC). It exercises, end to end with **no pre-shared
key**, the three things this increment adds:

1. **Service-mode accept loop** — the helper is launched **elevated** in `--service`
   mode (the long-lived, multi-connection loop) and serves connection after connection.
2. **Multi-caller auth** — the helper is provisioned to trust **two** images:
   `--app-path` (the interactive App) **and** `--reconciler-path` (the reconciler host),
   each by path + SHA-256. The smoke exe is provisioned as the trusted *reconciler*
   image, so it can connect **as the reconciler**.
3. **Connect-only reconciler client** — `HostElevatedClientFactory.CreateConnectOnly()`
   connects to the already-running helper service (**no `runas` launch**) and round-trips
   a harmless allowlisted op (`Ping`) over the negotiated, signed, allowlisted typed IPC.
   A second `Ping` proves the service loop re-armed and served the next connection.

The typed protocol / allowlist / HMAC / codec are **untouched** — only the outer
service loop, the multi-caller authenticator, and the connect-only wiring are new.

## Run (PowerShell)

```powershell
# 1) Build it (the ProjectReference copies the helper exe next to this tool):
dotnet build tools/reconcilerhelpersmoke/reconcilerhelpersmoke.csproj -c Debug

# 2) Run it — APPROVE the single UAC prompt it raises to launch the helper service:
tools\reconcilerhelpersmoke\bin\Debug\net8.0-windows\reconcilerhelpersmoke.exe
```

Each step prints `[PASS]` / `[FAIL]`; exit `0` = all PASS. The elevated helper keeps
running after the smoke (end its process to stop the service loop). The smoke uses a
unique pipe name per run so it never collides with a real App/helper session.

> NOTE: no session key is pre-shared — it is **negotiated** over the pipe *after* the
> helper authenticates this exe as the trusted reconciler image (§9.13 handshake).
