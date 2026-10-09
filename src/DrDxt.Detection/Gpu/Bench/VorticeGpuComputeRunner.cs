using System.Reflection;
using System.Runtime.InteropServices;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using SharpResult = SharpGen.Runtime.Result;

namespace DrDxt.Detection.Gpu.Bench;

// -----------------------------------------------------------------------
// R2 GPU bench — the THIN real-D3D11 IGpuComputeRunner (Vortice).
//
// This is the only real-D3D11 surface in the GPU bench. It runs the deterministic
// compute workload (GpuBench.hlsl / embedded GpuBench.cso) for a bounded time, reads
// the per-element output back, folds it with the SHARED GpuBenchWorkload.Fold (so the
// checksum matches the CPU known-answer), and reports device health via
// GetDeviceRemovedReason(). It is read/COMPUTE only — no NVAPI, no GPU tuning write,
// no elevation. It never throws across the seam: every failure path returns a clean
// Result.Fail or a GpuDeviceStatus.Error / DeviceLost outcome.
//
// Not unit-tested (no GPU in CI) — covered by a live smoke (tools/r2bench), like the
// PresentMon spine. Keep this thin; the testable logic lives in Application.
// -----------------------------------------------------------------------
public sealed class VorticeGpuComputeRunner : IGpuComputeRunner
{
    private const string ShaderResourceName = "DrDxt.Detection.Gpu.Bench.GpuBench.cso";
    private const int ThreadGroupSize = 256;   // must match [numthreads(256,1,1)] in the HLSL.

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint Seed;
        public uint Iterations;
        public uint ElementCount;
        public uint Pad;   // cbuffers are 16-byte aligned.
    }

    public Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            return RunCore(spec, duration, ct);
        }
        catch (Exception ex)
        {
            // Any unexpected failure → clean Fail (the probe maps this to CouldNotRun →
            // Inconclusive → revert). Never throw across the seam.
            return Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"GPU compute runner failed: {ex.Message}");
        }
    }

    private static Result<GpuComputeOutcome> RunCore(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct)
    {
        var bytecode = LoadShaderBytecode();
        if (bytecode is null)
            return Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Embedded compute-shader bytecode not found.");

        // Hardware device, FL11 (cs_5_0). Single-threaded: we own all calls here.
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var hr = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.Singlethreaded, featureLevels,
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();
            return Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"D3D11CreateDevice failed (0x{hr.Code:X8}).");
        }

        using (device)
        using (context)
        {
            ID3D11ComputeShader? shader = null;
            ID3D11Buffer? outputBuffer = null;
            ID3D11Buffer? stagingBuffer = null;
            ID3D11Buffer? paramsBuffer = null;
            ID3D11UnorderedAccessView? uav = null;
            try
            {
                shader = CreateComputeShader(device, bytecode);

                var initial = new uint[spec.ElementCount];   // zero-initialised output.
                outputBuffer = device.CreateBuffer<uint>(
                    initial.AsSpan(),
                    BindFlags.UnorderedAccess,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.BufferStructured,
                    structureByteStride: sizeof(uint));

                uav = device.CreateUnorderedAccessView(outputBuffer,
                    new UnorderedAccessViewDescription(outputBuffer, Format.Unknown,
                        firstElement: 0, numElements: (uint)spec.ElementCount,
                        BufferUnorderedAccessViewFlags.None));

                var p = new[]
                {
                    new Params
                    {
                        Seed = spec.Seed,
                        Iterations = (uint)spec.Iterations,
                        ElementCount = (uint)spec.ElementCount,
                        Pad = 0,
                    },
                };
                paramsBuffer = device.CreateBuffer<Params>(p.AsSpan(),
                    BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None);

                stagingBuffer = device.CreateBuffer(new BufferDescription(
                    (uint)(spec.ElementCount * sizeof(uint)),
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read,
                    ResourceOptionFlags.None, sizeof(uint)));

                context.CSSetShader(shader);
                context.CSSetConstantBuffer(0, paramsBuffer);
                context.CSSetUnorderedAccessView(0, uav);

                uint groups = (uint)((spec.ElementCount + ThreadGroupSize - 1) / ThreadGroupSize);

                // Bounded load: re-dispatch the same deterministic workload until the time
                // budget elapses (always at least once). The final buffer contents are
                // identical regardless of dispatch count, so the checksum stays deterministic;
                // dispatchCount only scales the throughput score.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long dispatchCount = 0;
                do
                {
                    context.Dispatch(groups, 1, 1);
                    dispatchCount++;
                    context.Flush();                 // submit work this pass.
                    if (ct.IsCancellationRequested) break;
                }
                while (sw.Elapsed < duration);
                sw.Stop();

                // Device health after the load (a TDR shows up here).
                SharpResult removed = device.DeviceRemovedReason;
                if (removed.Failure)
                    return Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(
                        GpuDeviceStatus.DeviceLost, 0, 0, sw.Elapsed.TotalMilliseconds));

                // Read back the per-element output and fold to the checksum.
                context.CopyResource(stagingBuffer, outputBuffer);
                var mapped = context.Map(stagingBuffer, 0, MapMode.Read, MapFlags.None);
                ulong checksum;
                try
                {
                    var span = MarshalSpan(mapped.DataPointer, spec.ElementCount);
                    checksum = GpuBenchWorkload.Fold(span);
                }
                finally
                {
                    context.Unmap(stagingBuffer, 0);
                }

                long iterationsCompleted = (long)spec.Iterations * dispatchCount;
                return Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(
                    GpuDeviceStatus.Healthy, checksum,
                    iterationsCompleted > int.MaxValue ? int.MaxValue : (int)iterationsCompleted,
                    sw.Elapsed.TotalMilliseconds));
            }
            finally
            {
                uav?.Dispose();
                stagingBuffer?.Dispose();
                paramsBuffer?.Dispose();
                outputBuffer?.Dispose();
                shader?.Dispose();
            }
        }
    }

    private static byte[]? LoadShaderBytecode()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ShaderResourceName);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static unsafe ID3D11ComputeShader CreateComputeShader(ID3D11Device device, byte[] bytecode)
    {
        fixed (byte* p = bytecode)
            return device.CreateComputeShader(p, (nuint)bytecode.Length, null);
    }

    private static unsafe ReadOnlySpan<uint> MarshalSpan(IntPtr dataPointer, int count)
        => new ReadOnlySpan<uint>((void*)dataPointer, count);
}
