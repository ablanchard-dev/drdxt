using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Win32_BIOS et Win32_BaseBoard ne changent pas pendant un scan : une requête WMI chacun sert la version et la date du
// BIOS, le fabricant et le modèle de la carte à toutes les familles qui les lisent (jusqu'à trois sur un Ryzen).
public sealed class UnScanInterrogeLeBiosUneFoisTests
{
    private sealed class WmiQuiCompte : IWmiQuery
    {
        public int RequetesBios;
        public int RequetesCarte;

        public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);

        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
        {
            if (wql.Contains("Win32_BIOS", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref RequetesBios);
                return Result<IReadOnlyList<WmiRow>>.Ok([new WmiRow(new Dictionary<string, object?>
                    { ["SMBIOSBIOSVersion"] = "1.00", ["ReleaseDate"] = "20250101000000.000000+000" })]);
            }
            if (wql.Contains("Win32_BaseBoard", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref RequetesCarte);
                return Result<IReadOnlyList<WmiRow>>.Ok([new WmiRow(new Dictionary<string, object?>
                    { ["Manufacturer"] = "Fabricant", ["Product"] = "Carte" })]);
            }
            return Result<IReadOnlyList<WmiRow>>.Ok([]);
        }
    }

    [Fact]
    public void Un_scan_interroge_le_BIOS_et_la_carte_une_seule_fois()
    {
        var wmi = new WmiQuiCompte();
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), wmi);

        var constats = audit.Lire();

        wmi.RequetesBios.Should().Be(1);
        wmi.RequetesCarte.Should().Be(1);
        constats.Should().Contain(c => c.Nom == "carte mere / firmware" && c.ValeurCourante!.Contains("Carte"),
            "témoin : la lecture unique sert bien les constats");
        audit.Lire();
        wmi.RequetesBios.Should().Be(2, "un nouveau scan relit : un BIOS mis à jour entre deux scans doit se voir");
    }
}
