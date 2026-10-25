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
        var board = _board.Detect();
        if (!board.IsOk) return Fail(board.Error!.Value);
        // La mémoire et le châssis ont leur « non lu » dans l'inventaire (aucune barrette, châssis inconnu), que le produit sait
        // dire : une lecture WMI manquée (winmgmt lent au moment du scan) ne fait pas tomber tout le scan. Le processeur, la
        // carte graphique et la carte mère restent indispensables.
        var mem = _mem.Detect();
        var chassis = _chassis.Detect();

        return Result<HardwareInventory>.Ok(new HardwareInventory(
            cpu.Value!, gpu.Value!, mem.IsOk ? mem.Value! : [], board.Value!.Board, board.Value.Bios,
            chassis.IsOk ? chassis.Value : ChassisType.Unknown, _mem.Emplacements()));
    });

    private static Result<HardwareInventory> Fail(Error e) => Result<HardwareInventory>.Fail(e.Code, e.Message);
}
