using DrDxt.Core;
using DrDxt.Detection.Wmi;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// WmiGpuDriverProbe : IGpuDriverProbe sur la couture IWmiQuery existante.
//
// Interroge Win32_VideoController et rend le DriverVersion de l'adaptateur dont le constructeur
// correspond (sous-chaîne sans tenir compte de la casse sur AdapterCompatibility ou Name). Un
// changement de DriverVersion par constructeur depuis l'apply est le signal de dérive par mise à
// jour du pilote (plan T8b/T8c).
//
// Honnêteté de l'absence : aucun adaptateur correspondant (ou un adaptateur correspondant sans
// DriverVersion utilisable) → Ok(null) : le constructeur n'est simplement pas présent, ce n'est
// pas une erreur. Seul un vrai échec de requête WMI → Fail. La frontière WMI est le
// ManagementWmiQuery existant, qui emballe déjà chaque exception de System.Management dans un Result.
// -----------------------------------------------------------------------
public sealed class WmiGpuDriverProbe : IGpuDriverProbe
{
    private const string VideoControllerQuery =
        "SELECT DriverVersion, AdapterCompatibility, Name FROM Win32_VideoController";

    private readonly IWmiQuery _wmi;

    public WmiGpuDriverProbe(IWmiQuery wmi) => _wmi = wmi;

    public Result<string?> ReadDriverVersion(string vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return Result<string?>.Ok(null);

        var rows = _wmi.Query(VideoControllerQuery);
        if (!rows.IsOk)
            return Result<string?>.Fail(rows.Error!.Value.Code, rows.Error.Value.Message);

        foreach (var row in rows.Value!)
        {
            var compat = row.GetString("AdapterCompatibility");
            var name = row.GetString("Name");

            var matches =
                (compat is not null && compat.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                || (name is not null && name.Contains(vendor, StringComparison.OrdinalIgnoreCase));

            if (!matches)
                continue;

            var driverVersion = row.GetString("DriverVersion");
            // Un adaptateur correspondant sans version utilisable est traité comme « absent » (Ok(null)),
            // pas comme une erreur : le signal de dérive n'a simplement pas de valeur à comparer à ce tour.
            return Result<string?>.Ok(
                string.IsNullOrWhiteSpace(driverVersion) ? null : driverVersion);
        }

        // Aucun adaptateur pour ce constructeur : l'absence n'est pas une erreur.
        return Result<string?>.Ok(null);
    }
}
