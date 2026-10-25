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
// Sans GPU en CI, seuls le choix de la carte et la boucle de charge (Charger) ont des tests unitaires ;
// la charge réelle n'en a pas. À garder fin ; la logique testable vit dans Application.
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

    /// <summary>Plus d'une carte dédiée (la règle du produit, mémoire et nom) : rien ne relie l'index de la carte réglée
    /// (NVAPI, ADLX) à l'adaptateur « haute performance » que charge le banc, qui ne sait donc pas laquelle il juge.</summary>
    internal static bool PlusieursCartesDediees(IReadOnlyList<DxgiAdapter> cartes)
        => cartes.Count(c => CartesGraphiques.EstDediee(
            new GpuInfo(c.Description, GpuDetector.VendorName(c.VendorId), c.DedicatedVramBytes, "", TriState.Unknown))) > 1;

    private static IDXGIAdapter1? AdaptateurHautePerformance()
    {
        try
        {
            using var fabrique = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            return fabrique.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? a).Success ? a : null;
        }
        catch (Exception) { return null; }
    }

    private static Result<GpuComputeOutcome> RunCore(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct)
    {
        // Pas de verdict sur une carte peut-être jamais chargée : le banc rend « non concluant », jamais « stable ».
        if (new VorticeDxgiProvider().EnumerateAdapters() is { IsOk: true, Value: { } cartes } && PlusieursCartesDediees(cartes))
            return Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Several dedicated graphics cards: the one the bench loads is not known, no stability verdict.");
        var bytecode = LoadShaderBytecode();
        if (bytecode is null)
            return Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable,
                "Embedded compute-shader bytecode not found.");

        // Périphérique matériel, FL11 (cs_5_0). Un seul thread : tous les appels passent par ici.
        // Sur l'adaptateur « haute performance » de DXGI (IDXGIFactory6::EnumAdapterByGpuPreference), la carte dédiée
        // que les réglages visent : l'adaptateur par défaut est la puce intégrée sur un portable hybride, et le banc y
        // jugeait la stabilité d'une carte qu'il ne chargeait pas. Avec un adaptateur, le type de pilote doit être
        // Unknown (doc D3D11CreateDevice). Sans IDXGIFactory6 (Windows 10 antérieur à 1803), l'adaptateur par défaut.
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        using var adaptateur = AdaptateurHautePerformance();
        var hr = D3D11.D3D11CreateDevice(
            adaptateur, adaptateur is null ? DriverType.Hardware : DriverType.Unknown, DeviceCreationFlags.Singlethreaded, featureLevels,
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
                var (dispatchCount, complete) = Charger(() =>
                {
                    context.Dispatch(groups, 1, 1);
                    context.Flush();                 // soumet le travail de cette passe.
                }, duration, ct);
                sw.Stop();

                // Santé du périphérique après la charge (un TDR apparaît ici).
                SharpResult removed = device.DeviceRemovedReason;
                if (removed.Failure)
                    return Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(
                        GpuDeviceStatus.DeviceLost, 0, 0, sw.Elapsed.TotalMilliseconds));

                // Une charge coupée avant sa durée ne prouve aucune stabilité : jamais « sain ».
                if (!complete)
                    return Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(
                        GpuDeviceStatus.Error, 0, 0, sw.Elapsed.TotalMilliseconds));

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

    /// <summary>Relance <paramref name="passe"/> jusqu'à épuisement de <paramref name="duree"/> (au moins une fois) ;
    /// <c>Complete</c> est faux si <paramref name="ct"/> a coupé la charge.</summary>
    internal static (long Lancees, bool Complete) Charger(Action passe, TimeSpan duree, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lancees = 0;
        do
        {
            passe();
            lancees++;
            if (ct.IsCancellationRequested) return (lancees, false);
        }
        while (sw.Elapsed < duree);
        return (lancees, true);
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
