using DrDxt.Core;

namespace DrDxt.Profiles.Tests;

// Builders for M1 HardwareInventory fixtures — keep the chip-relevant fields explicit
// and fill the rest with innocuous defaults so each test reads as "this chip → this".
internal static class TestInventory
{
    public const long EightGiB = 8L << 30;

    public static CpuInfo AmdX3dDualCcd(string model = "AMD Ryzen 9 7950X3D 16-Core Processor") =>
        new(model, "AuthenticAMD", Cores: 16, Threads: 32, IsX3D: true, CcdCount: 2);

    public static CpuInfo AmdX3dSingleCcd(string model = "AMD Ryzen 7 7800X3D 8-Core Processor") =>
        new(model, "AuthenticAMD", Cores: 8, Threads: 16, IsX3D: true, CcdCount: 1);

    public static CpuInfo AmdNonX3d() =>
        new("AMD Ryzen 7 7700X 8-Core Processor", "AuthenticAMD", 8, 16, IsX3D: false, CcdCount: 1);

    public static CpuInfo Intel(string model = "13th Gen Intel(R) Core(TM) i7-14700F") =>
        new(model, "GenuineIntel", Cores: 20, Threads: 28, IsX3D: false, CcdCount: 0);

    public static GpuInfo NvidiaRtxDesktop(string model = "NVIDIA GeForce RTX 4070") =>
        new(model, "NVIDIA", VramBytes: 12L << 30, DriverVersion: "560.94", ReBar: TriState.On);

    public static GpuInfo NvidiaRtxMobile() =>
        new("NVIDIA GeForce RTX 4070 Laptop GPU", "NVIDIA", 8L << 30, "560.94", TriState.On);

    public static GpuInfo NvidiaQuadro() =>
        new("NVIDIA Quadro P2000", "NVIDIA", 5L << 30, "552.00", TriState.Unknown);

    public static GpuInfo AmdRadeonDesktop() =>
        new("AMD Radeon RX 7900 XTX", "Advanced Micro Devices, Inc.", 24L << 30, "24.10.1", TriState.On);

    public static GpuInfo IntelIgpu() =>
        new("Intel(R) UHD Graphics 770", "Intel Corporation", VramBytes: 128L << 20, DriverVersion: "31.0", ReBar: TriState.Unknown);

    public static HardwareInventory Build(
        CpuInfo cpu,
        IReadOnlyList<GpuInfo>? gpus = null,
        ChassisType chassis = ChassisType.Desktop) =>
        new(
            cpu,
            gpus ?? new[] { IntelIgpu() },
            new[] { new MemoryModule("KF560C36-16", "Kingston", 6000, 6000) },
            new MotherboardInfo("ASUS", "ROG STRIX B650-A"),
            new BiosInfo("1.2.3", DateTimeOffset.UnixEpoch, IsUefi: true),
            chassis);
}
