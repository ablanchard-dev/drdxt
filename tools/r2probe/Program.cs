using DrDxt.ElevatedHelper.Execution.NvApi;

// Read-only validation of the reverse-engineered NVAPI tuning struct layout against a
// REAL local NVIDIA GPU. Calls ONLY the Get* methods (GetPstates20 /
// ClientPowerPoliciesGetStatus) — no writes, no elevation. If these return Ok with
// sane values, the C# struct layout matches the driver's C structs (the driver
// accepted our version word). If they fail with a status, the layout is wrong.
Console.WriteLine("== R2 read-only NVAPI layout probe (no write, no elevation) ==");

var tuning = new NvApiTuningActions();

var core = tuning.GetCoreClockOffsetMhz(0);
Console.WriteLine(core.IsOk
    ? $"  GetCoreClockOffsetMhz(0)  = OK  -> current P0 graphics offset = {core.Value} MHz"
    : $"  GetCoreClockOffsetMhz(0)  = FAIL ({core.Error?.Code}: {core.Error?.Message})");

var power = tuning.GetPowerLimitPercent(0);
Console.WriteLine(power.IsOk
    ? $"  GetPowerLimitPercent(0)   = OK  -> current power limit = {power.Value} %"
    : $"  GetPowerLimitPercent(0)   = FAIL ({power.Error?.Code}: {power.Error?.Message})");

Console.WriteLine(core.IsOk && power.IsOk
    ? "RESULT: layout validated live on this GPU (both reads succeeded)."
    : "RESULT: at least one read failed — see status above (layout or driver issue).");
