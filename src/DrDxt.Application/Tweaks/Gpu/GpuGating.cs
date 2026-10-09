using System.Text.RegularExpressions;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Shared HARD hardware gating for the M2 Phase E GPU-tuning tweaks (master §16).
//
// GPU tuning (undervolt / power-limit / fan-curve) is desktop dGPU only. It is
// refused on:
//   * laptops / Optimus mobile GPUs (mobile GPUs are driver-locked — guided in M3),
//   * integrated GPUs (Intel UHD/Iris, AMD APU iGPUs — nothing meaningful to tune),
//   * handhelds (locked firmware power profiles).
//
// Pure, side-effect-free predicates so E2/E3/E4 can gate identically and be unit-
// tested in isolation.
// -----------------------------------------------------------------------
internal static class GpuGating
{
    /// <summary>True only on a desktop that has a discrete (non-mobile, non-iGPU)
    /// NVIDIA or AMD render GPU. False on laptop / Optimus / iGPU / handheld.</summary>
    public static bool HasDesktopDiscreteGpu(HardwareInventory hw)
    {
        if (hw.Chassis != ChassisType.Desktop)
            return false;
        return hw.Gpus.Any(IsTunableDiscreteGpu);
    }

    /// <summary>True if this specific GPU is a tunable discrete NVIDIA or AMD GPU
    /// (dedicated VRAM, not integrated, not a mobile / Max-Q part).</summary>
    public static bool IsTunableDiscreteGpu(GpuInfo g) =>
        (IsNvidia(g) || IsAmd(g)) && IsDiscrete(g) && !IsMobileGpu(g);

    public static bool IsNvidia(GpuInfo g) =>
        g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public static bool IsAmd(GpuInfo g) =>
        g.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntel(GpuInfo g) =>
        g.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    // A discrete GPU has dedicated VRAM. Integrated GPUs report ~0 dedicated VRAM
    // (they share system memory) — we treat anything below 1 GiB as integrated.
    private static bool IsDiscrete(GpuInfo g) =>
        !IsIntel(g) && g.VramBytes >= (1L << 30);

    // Mobile GPUs are driver-locked (no user clock / power / fan control via these paths).
    private static bool IsMobileGpu(GpuInfo g) =>
        g.Model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this NVIDIA GPU supports direct voltage-undervolt writes.
    /// Blackwell (RTX 50) has the voltage path LOCKED OUT (recipe), so a later
    /// undervolt gate must use power-limit + core-offset there instead. Delegates to
    /// the public GpuArchitectureGate (one shared classifier).</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo g) =>
        GpuArchitectureGate.SupportsVoltageUndervolt(g);
}

// -----------------------------------------------------------------------
// GpuArchitectureGate — PUBLIC, pure NVIDIA architecture classifier + voltage-gate.
//
// Exposes the Blackwell / RTX 50 detection so the GPU read seam (INvApiInfo) and a
// later voltage-undervolt gate share ONE source of truth (recipe "Blackwell / RTX 50
// lockout"). Pure + side-effect-free → fully unit-testable without any NVAPI call.
//
// Detection is by marketing name (e.g. "RTX 5080" → Blackwell, "RTX 4070" → Ada):
// the unelevated-safe, driver-independent signal already carried by GpuInfo.Model
// and by NVAPI's GPU name read. (NVAPI NvAPI_GPU_GetArchInfo would give the silicon
// architecture id directly but its function id is UNVERIFIED in the recipe — the
// name classifier is the verified, fail-clean path used here.)
// -----------------------------------------------------------------------
public static class GpuArchitectureGate
{
    // "RTX 50xx" (any 50-series, 4-digit model starting 50) = Blackwell.
    private static readonly Regex BlackwellModel =
        new(@"\bRTX\s*50\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "RTX 40xx/30xx/20xx", "GTX 16xx/10xx" = Ada or older (legacy voltage writes OK).
    private static readonly Regex AdaOrOlderModel =
        new(@"\b(RTX\s*(40|30|20)\d{2}|GTX\s*(16|10)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // « RX 7xxx » = RDNA 3, « RX 9xxx » = RDNA 4. Meme discipline que cote NVIDIA : le nom
    // commercial est le signal lisible sans elevation et sans driver, donc utilisable avant
    // l'achat — ce que D5 exige de la matrice de couverture.
    private static readonly Regex Rdna3Model =
        new(@"\bRX\s*7\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna2Model =
        new(@"\bRX\s*6\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna4Model =
        new(@"\bRX\s*9\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Classifies an NVIDIA GPU marketing name into a coarse architecture.
    /// Unrecognized / non-NVIDIA names → Unknown (gates treat that conservatively).</summary>
    public static GpuArchitecture ClassifyByName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return GpuArchitecture.Unknown;
        if (BlackwellModel.IsMatch(model))
            return GpuArchitecture.Blackwell;
        if (AdaOrOlderModel.IsMatch(model))
            return GpuArchitecture.AdaOrOlder;
        if (Rdna4Model.IsMatch(model))
            return GpuArchitecture.Rdna4;
        if (Rdna3Model.IsMatch(model))
            return GpuArchitecture.Rdna3;
        if (Rdna2Model.IsMatch(model))
            return GpuArchitecture.Rdna2;
        return GpuArchitecture.Unknown;
    }

    /// <summary>True when a GPU of this architecture honours direct voltage-undervolt
    /// writes. Blackwell is locked out; Ada-or-older is allowed; Unknown is treated
    /// conservatively as not supported (never assume a write is safe).</summary>
    public static bool SupportsVoltageUndervolt(GpuArchitecture architecture) => architecture switch
    {
        // Validees en ecriture sur une vraie carte.
        GpuArchitecture.AdaOrOlder => true,
        GpuArchitecture.Rdna4 => true,
        // Blackwell : NVIDIA a verrouille le chemin voltage.
        GpuArchitecture.Blackwell => false,
        // RDNA3 (D5) : la seule architecture AMD qui garde le voltage manuel, et la seule que
        // personne n'a jamais testee en ecriture. Ecrire un voltage sur du materiel jamais valide
        // est exactement ce que « fail-closed materiel » interdit.
        GpuArchitecture.Rdna3 => false,
        // Une architecture qu'on ne sait pas nommer n'est pas une architecture validee.
        _ => false,
    };

    /// <summary>True when this GPU supports direct voltage-undervolt writes. Blackwell
    /// (RTX 50) NVIDIA GPUs are gated OFF (use power-limit + core-offset instead);
    /// non-NVIDIA GPUs return true (AMD ADLX undervolt is a separate, unaffected path).</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo gpu)
    {
        var architecture = ClassifyByName(gpu.Model);

        // Corrige le 02/09/2026 (D5). Cette methode rendait `true` pour tout GPU non-NVIDIA,
        // au motif que « le verrou Blackwell ne concerne pas AMD ». C'etait vrai, et insuffisant :
        // ca laissait passer RDNA3, la seule architecture AMD qui garde le voltage manuel et la
        // seule que personne n'a jamais testee en ecriture. Un test encodait meme ce
        // comportement sur une RX 7900 XTX.
        // AMD-TOUT (09/10/2026) : RDNA2 pareil, jamais testée en écriture.
        if (architecture is GpuArchitecture.Rdna3 or GpuArchitecture.Rdna2)
            return false;

        var isNvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        if (!isNvidia)
            // Les autres cartes AMD gardent leur chemin ADLX : D5 nomme RDNA3, et elargir le
            // refus a toutes les cartes non classees couperait des machines sur une supposition
            // plutot que sur un constat. La matrice de couverture, elle, DIT ce qu'on ignore.
            return true;

        return architecture != GpuArchitecture.Blackwell;
    }
}
