using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Hardware;

namespace DrDxt.Detection;

public sealed class HardwareDetector : IHardwareDetector
{
    private readonly CpuDetector _cpu;
    private readonly GpuDetector _gpu;
    private readonly MemoryDetector _mem;
    private readonly BoardBiosDetector _board;
    private readonly ChassisDetector _chassis;

    public HardwareDetector(CpuDetector cpu, GpuDetector gpu, MemoryDetector mem,
                            BoardBiosDetector board, ChassisDetector chassis)
        => (_cpu, _gpu, _mem, _board, _chassis) = (cpu, gpu, mem, board, chassis);

    public Task<Result<HardwareInventory>> DetectAsync() => Task.Run(() =>
    {
        var cpu = _cpu.Detect();
        if (!cpu.IsOk) return Fail(cpu.Error!.Value);
        var gpu = _gpu.Detect();
        if (!gpu.IsOk) return Fail(gpu.Error!.Value);
        var mem = _mem.Detect();
        if (!mem.IsOk) return Fail(mem.Error!.Value);
        var board = _board.Detect();
        if (!board.IsOk) return Fail(board.Error!.Value);
        var chassis = _chassis.Detect();
        if (!chassis.IsOk) return Fail(chassis.Error!.Value);

        return Result<HardwareInventory>.Ok(new HardwareInventory(
            cpu.Value!, gpu.Value!, mem.Value!, board.Value!.Board, board.Value.Bios, chassis.Value, _mem.Emplacements()));
    });

    private static Result<HardwareInventory> Fail(Error e) => Result<HardwareInventory>.Fail(e.Code, e.Message);
}
