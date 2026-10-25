using DrDxt.Core;
using DrDxt.Detection.Wmi;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.Detection.Gpu;

public sealed class GpuDetector
{
    private const int VendorNvidia = 0x10DE;
    private const int VendorAmd    = 0x1002;
    private const int VendorIntel  = 0x8086;

    private readonly IDxgiProvider    _dxgi;
    private readonly IAdlxProvider    _adlx;
    private readonly IWmiQuery        _wmi;
    private readonly INvReBarProvider _nvReBar;
    private readonly INvmlNative     _nvml;

    public GpuDetector(IDxgiProvider dxgi, IAdlxProvider adlx, IWmiQuery wmi, INvReBarProvider nvReBar, INvmlNative nvml)
        => (_dxgi, _adlx, _wmi, _nvReBar, _nvml) = (dxgi, adlx, wmi, nvReBar, nvml);

    public Result<IReadOnlyList<GpuInfo>> Detect()
    {
        var adapters = _dxgi.EnumerateAdapters();
        if (!adapters.IsOk)
            return Result<IReadOnlyList<GpuInfo>>.Fail(
                adapters.Error!.Value.Code, adapters.Error.Value.Message);

        var controleurs = ReadControllers();

        // La branche NVIDIA reelle, via NVML (nvmlSystemGetDriverVersion → "610.88"). Elle est
        // system-wide : un seul pilote sert toutes les cartes NVIDIA de la machine.
        //
        // Pourquoi ça compte, et pas qu'esthétiquement : WMI rend la quadruplette WDDM
        // ("32.0.16.1088"), dont le premier groupe est la version WDDM/DirectX. Le levier de fraîcheur
        // refuse de juger cette forme (à raison : la juger contre le plancher NVIDIA marquerait
        // toutes les machines obsolètes) ; sans la branche NVML, le levier « pilote obsolète » ne se
        // déclencherait jamais sur une machine NVIDIA.
        // Seulement s'il y a une carte NVIDIA : sinon on forcerait le chargement de nvml.dll sur
        // une machine AMD ou Intel pure, pour un résultat dont personne ne se sert.
        string? nvidiaBranch = adapters.Value!.Any(a => a.VendorId == VendorNvidia)
                               && _nvml.TryGetDriverVersion(out var branch) ? branch : null;

        // Le ReBAR NVIDIA se lit sur le device NVML 0. Avec une seule carte NVIDIA, c'est elle ;
        // avec plusieurs, rien ne dit laquelle est le device 0 : « inconnu » plutot que l'etat
        // d'une autre carte (meme regle que la version de pilote).
        var uneSeuleNvidia = adapters.Value!.Count(a => a.VendorId == VendorNvidia) == 1;

        var gpus = adapters.Value!.Select(a =>
        {
            var vendor = VendorName(a.VendorId);
            var rebar = a.VendorId switch
            {
                VendorAmd    => _adlx.GetSmartAccessMemoryState(),       // SAM via ADLX
                VendorNvidia => uneSeuleNvidia ? ReBarNvidia(_nvReBar.GetReBarState(), a.Description) : TriState.Unknown, // NVML BAR1, device 0
                _            => TriState.Unknown                         // Intel: pas d'API → Unknown
            };
            // NVML seulement pour NVIDIA, et seulement si la lecture a marché : sinon on garde
            // la valeur WMI. Jamais de version fabriquée, jamais la branche NVIDIA collée sur une
            // carte AMD ou Intel.
            var driverVersion = a.VendorId == VendorNvidia && nvidiaBranch is not null
                ? nvidiaBranch
                : DriverVersionFor(a, controleurs);
            return new GpuInfo(a.Description, vendor, a.DedicatedVramBytes, driverVersion, rebar);
        }).ToList();

        return Result<IReadOnlyList<GpuInfo>>.Ok(gpus);
    }

    /// <summary>NVIDIA ne prend en charge le Resizable BAR qu'à partir des GeForce RTX 30 (NVIDIA, « GeForce RTX 30 Series
    /// Resizable BAR ») : sur une carte plus ancienne, la fenêtre BAR1 classique est la seule possible, pas un réglage coupé.
    /// Seul Off dépend du modèle ; On reste lu tel quel.</summary>
    internal static TriState ReBarNvidia(TriState lu, string description)
        => lu == TriState.Off && !System.Text.RegularExpressions.Regex.IsMatch(description, @"\bRTX\s*[3-9]0[0-9]{2}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? TriState.Unknown : lu;

    // Une ligne Win32_VideoController par controleur. Sur une machine iGPU + carte dediee, prendre
    // la premiere collerait le pilote de l'iGPU sur la carte. La ligne se retrouve par son nom (WMI
    // Name et DXGI Description viennent tous deux du DriverDesc du pilote : identiques, relevé sur une RTX 4060),
    // sinon par le fabricant dans PNPDeviceID (« PCI\VEN_10DE&... ») s'il n'y en a
    // qu'une. Seule ligne de la machine : c'est la sienne. Sinon « Unknown », jamais le pilote d'un
    // autre controleur.
    private IReadOnlyList<WmiRow> ReadControllers()
    {
        var q = _wmi.Query("SELECT DriverVersion, Name, PNPDeviceID FROM Win32_VideoController");
        return q.IsOk ? q.Value! : [];
    }

    internal static string DriverVersionFor(DxgiAdapter a, IReadOnlyList<WmiRow> controleurs)
    {
        var ligne = controleurs.FirstOrDefault(c =>
            string.Equals(c.GetString("Name")?.Trim(), a.Description.Trim(), StringComparison.OrdinalIgnoreCase));
        if (ligne is null)
        {
            var ven = $"VEN_{a.VendorId:X4}";
            var memeFabricant = controleurs
                .Where(c => c.GetString("PNPDeviceID")?.Contains(ven, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            ligne = memeFabricant.Count == 1 ? memeFabricant[0]
                  : controleurs.Count == 1 ? controleurs[0]
                  : null;
        }
        return ligne?.GetString("DriverVersion") ?? "Unknown";
    }

    internal static string VendorName(int id) => id switch
    {
        VendorNvidia => "NVIDIA",
        VendorAmd    => "AMD",
        VendorIntel  => "Intel",
        _            => "Unknown"
    };
}
