using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

public sealed record BoardBios(MotherboardInfo Board, BiosInfo Bios);

public sealed class BoardBiosDetector
{
    private readonly IWmiQuery _wmi;
    private readonly IFirmwareProvider _firmware;
    public BoardBiosDetector(IWmiQuery wmi, IFirmwareProvider firmware) => (_wmi, _firmware) = (wmi, firmware);

    public Result<BoardBios> Detect()
    {
        var bq = _wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        if (!bq.IsOk) return Result<BoardBios>.Fail(bq.Error!.Value.Code, bq.Error.Value.Message);
        var board = bq.Value!.FirstOrDefault();
        var motherboard = new MotherboardInfo(
            board?.GetString("Manufacturer") ?? "Unknown",
            board?.GetString("Product") ?? "Unknown");

        var biosQ = _wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        if (!biosQ.IsOk) return Result<BoardBios>.Fail(biosQ.Error!.Value.Code, biosQ.Error.Value.Message);
        var biosRow = biosQ.Value!.FirstOrDefault();
        var bios = new BiosInfo(
            biosRow?.GetString("SMBIOSBIOSVersion") ?? "Unknown",
            ParseSmbiosDate(biosRow?.GetString("ReleaseDate")),
            _firmware.IsUefi());

        return Result<BoardBios>.Ok(new BoardBios(motherboard, bios));
    }

    private static DateTimeOffset? ParseSmbiosDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return null;
        return DateTime.TryParseExact(raw[..8], "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d, TimeSpan.Zero) : null;
    }
}
