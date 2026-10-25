using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Un onduleur branché en USB apparaît dans Win32_Battery : le PC de bureau qu'il protège n'est pas un portable, et ses
// limites de puissance ou son branchement d'écran ne se jugent pas comme ceux d'un portable.
public sealed class OnduleurNEstPasUnPortableTests
{
    [Theory]
    [InlineData(3, false)]   // Desktop
    [InlineData(10, true)]   // Notebook
    [InlineData(2, true)]    // Unknown : la batterie fait foi
    public void La_batterie_d_un_boitier_de_bureau_est_un_onduleur(int chassis, bool portable)
        => RepairAuditService.Batterie(new Wmi(batterie: true, chassis)).Should().Be(portable);

    [Fact]
    public void Sans_batterie_ce_n_est_pas_un_portable()
        => RepairAuditService.Batterie(new Wmi(batterie: false, 10)).Should().BeFalse();

    private sealed class Wmi(bool batterie, int chassis) : IWmiQuery
    {
        public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);
        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql) => Result<IReadOnlyList<WmiRow>>.Ok(
            wql.Contains("Win32_Battery") ? (batterie ? [Ligne("DeviceID", "UPS")] : [])
            : wql.Contains("Win32_SystemEnclosure") ? [Ligne("ChassisTypes", new ushort[] { (ushort)chassis })]
            : wql.Contains("Win32_ComputerSystem") ? [Ligne("Model", "B760M")]
            : []);

        private static WmiRow Ligne(string cle, object valeur) => new(new Dictionary<string, object?> { [cle] = valeur });
    }
}
