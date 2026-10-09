// scene — un « jeu » que DrDXT controle, pour mesurer un reglage SANS joueur.
//
// Une fenetre, une swap chain D3D11 en flip model (comme un jeu moderne), et a chaque image :
// le shader de calcul DETERMINISTE du banc (vraie charge GPU, meme HLSL que GpuBench), puis un
// clear et Present(0) sans limite. PresentMon la voit exactement comme un jeu : l'Optimiser
// peut donc derouler A/A, A-B-A-B, garder ou annuler sans que personne ne joue.
//
// Ce qui est prouve sur cette scene vaut pour CETTE machine et CE chemin de rendu, pas pour
// Warzone : la scene ne remplace pas la mesure en jeu, elle permet de la preparer et de tester
// les leviers qui touchent le rendu (plan d'alimentation, HAGS, Game Mode, file de rendu).
//
//   scene [secondes=60] [iterationsParImage=32]
//
// Mesuré le 24/09/2026 sur la machine de référence (RTX 4060) :
//   - 32 itérations : la scène bute sur un plafond de 1 000 img/s, rien ne s'y mesure ;
//     4 096 itérations : 193 img/s, limitée par le GPU. Viser une charge sous tout plafond.
//   - sans chauffe, la scène dérive de +0,1 % par passe et l'optimiseur refuse de tester ;
//     après 5 min de chauffe, résolution de la moyenne ±0,1 % et contrôle complet (27 passes).
//   Recette : `scene 3000 4096`, attendre 5 min, puis `optimize <pid> 20 --controle`.
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

var secondes = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
var iterations = args.Length > 1 && uint.TryParse(args[1], out var it) ? it : 32u;
const int Elements = 1 << 20;
const int Groupe = 256;   // [numthreads(256,1,1)] dans GpuBench.hlsl

ApplicationConfiguration.Initialize();
using var fenetre = new Form { Text = "DrDXT - scene de mesure", ClientSize = new System.Drawing.Size(1280, 720) };
fenetre.Show();
fenetre.Activate();
// 24/09/2026 : lancée depuis un terminal, la scène restait DERRIÈRE lui (Windows refuse le premier
// plan à un processus qui ne l'a pas) et le repli DXGI, qui observe le focus, écartait toutes ses
// passes. On s'attache un instant au fil de la fenêtre de devant pour pouvoir prendre sa place.
PremierPlan.Prendre(fenetre.Handle);

D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? device, out _, out ID3D11DeviceContext? ctx).CheckError();
using var dev = device!;
using var context = ctx!;
using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
using var swap = factory.CreateSwapChainForHwnd(dev, fenetre.Handle, new SwapChainDescription1
{
    Width = 1280, Height = 720, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
    SwapEffect = SwapEffect.FlipDiscard, BufferUsage = Usage.RenderTargetOutput,
    SampleDescription = new SampleDescription(1, 0),
});
using var dos = swap.GetBuffer<ID3D11Texture2D>(0);
using var rtv = dev.CreateRenderTargetView(dos);

// Le shader du banc, lu dans la ressource embarquee de DrDxt.Detection.
byte[] cso;
using (var flux = typeof(DrDxt.Detection.Gpu.Bench.VorticeGpuComputeRunner).Assembly
           .GetManifestResourceStream("DrDxt.Detection.Gpu.Bench.GpuBench.cso")!)
using (var ms = new MemoryStream()) { flux.CopyTo(ms); cso = ms.ToArray(); }
using var cs = CreerShader(dev, cso);
using var sortie = dev.CreateBuffer<uint>(new uint[Elements].AsSpan(), BindFlags.UnorderedAccess, ResourceUsage.Default,
    CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, structureByteStride: sizeof(uint));
using var uav = dev.CreateUnorderedAccessView(sortie, new UnorderedAccessViewDescription(sortie, Format.Unknown, 0, Elements, BufferUnorderedAccessViewFlags.None));
using var parametres = dev.CreateBuffer<Params>(new[] { new Params { Seed = 0x9E3779B1u, Iterations = iterations, ElementCount = Elements } }.AsSpan(),
    BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None);

var t = System.Diagnostics.Stopwatch.StartNew();
long images = 0;
while (t.Elapsed.TotalSeconds < secondes && fenetre.Visible)
{
    context.CSSetShader(cs);
    context.CSSetConstantBuffer(0, parametres);
    context.CSSetUnorderedAccessView(0, uav);
    context.Dispatch((Elements + Groupe - 1) / Groupe, 1, 1);
    context.OMSetRenderTargets(rtv);
    var v = (float)(images % 256) / 255f;
    context.ClearRenderTargetView(rtv, new Color4(0.05f, v * 0.3f, 0.2f, 1f));
    swap.Present(0, PresentFlags.None);
    images++;
    Application.DoEvents();
}
Console.WriteLine($"scene : {images} images en {t.Elapsed.TotalSeconds:0.0} s = {images / t.Elapsed.TotalSeconds:0} img/s (iterations/image {iterations})");
return 0;

static unsafe ID3D11ComputeShader CreerShader(ID3D11Device d, byte[] b) { fixed (byte* p = b) return d.CreateComputeShader(p, (nuint)b.Length, null); }

[StructLayout(LayoutKind.Sequential)]
struct Params { public uint Seed; public uint Iterations; public uint ElementCount; public uint Pad; }

static class PremierPlan
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attacher);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

    public static void Prendre(IntPtr fenetre)
    {
        var devant = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var moi = GetCurrentThreadId();
        var attache = devant != 0 && devant != moi && AttachThreadInput(moi, devant, true);
        BringWindowToTop(fenetre);
        var pris = SetForegroundWindow(fenetre);
        if (attache) AttachThreadInput(moi, devant, false);
        Console.WriteLine(pris ? "scene : au premier plan" : "scene : premier plan refuse par Windows");
    }
}
