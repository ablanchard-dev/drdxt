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
// Banc GPU : le fin IGpuComputeRunner D3D11 réel (Vortice).
//
// La seule surface D3D11 réelle du banc GPU. Il lance la charge de calcul déterministe
// (GpuBench.hlsl / GpuBench.cso embarqué) pendant un temps borné, relit la sortie par élément,
// la replie avec le GpuBenchWorkload.Fold partagé (la somme de contrôle correspond ainsi à la
// réponse connue du CPU), et rapporte la santé du périphérique via GetDeviceRemovedReason().
// Lecture et calcul seulement : pas de NVAPI, pas d'écriture de réglage GPU, pas d'élévation.
// Ne lève jamais à travers la couture : chaque échec rend un Result.Fail propre ou un résultat
// GpuDeviceStatus.Error / DeviceLost.
//
// Pas de test unitaire (pas de GPU en CI) : couvert par un smoke test réel (tools/r2bench),
// comme la colonne PresentMon. À garder fin ; la logique testable vit dans Application.
// -----------------------------------------------------------------------
public sealed class VorticeGpuComputeRunner : IGpuComputeRunner
{
    private const string ShaderResourceName = "DrDxt.Detection.Gpu.Bench.GpuBench.cso";
    private const int ThreadGroupSize = 256;   // doit correspondre au [numthreads(256,1,1)] du HLSL.

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint Seed;
        public uint Iterations;
        public uint ElementCount;
        public uint Pad;   // les cbuffers sont alignés sur 16 octets.
    }

    public Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            return RunCore(spec, duration, ct);
        }
        catch (Exception ex)
        {
            // Tout échec imprévu → Fail propre (la sonde le traduit en CouldNotRun →
            // Inconclusive → retour). Jamais d'exception à travers la couture.
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

        // Périphérique matériel, FL11 (cs_5_0). Un seul thread : tous les appels passent par ici.
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

                var initial = new uint[spec.ElementCount];   // sortie initialisée à zéro.
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

                // Charge bornée : relancer la même charge déterministe jusqu'à épuisement du budget de
                // temps (au moins une fois). Le contenu final du tampon est identique quel que soit le nombre
                // de lancements : la somme de contrôle reste déterministe ; dispatchCount ne fait que
                // pondérer le score de débit.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long dispatchCount = 0;
                do
                {
                    context.Dispatch(groups, 1, 1);
                    dispatchCount++;
                    context.Flush();                 // soumet le travail de cette passe.
                    if (ct.IsCancellationRequested) break;
                }
                while (sw.Elapsed < duration);
                sw.Stop();

                // Santé du périphérique après la charge (un TDR apparaît ici).
                SharpResult removed = device.DeviceRemovedReason;
                if (removed.Failure)
                    return Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(
                        GpuDeviceStatus.DeviceLost, 0, 0, sw.Elapsed.TotalMilliseconds));

                // Relire la sortie par élément et la replier en somme de contrôle.
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
