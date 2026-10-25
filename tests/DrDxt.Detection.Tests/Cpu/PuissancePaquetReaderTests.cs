using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

/// <summary>Le compteur « Energy Meter » publie des milliwatts ; l'instance du paquet porte
/// « _PKG ». Lignes relevees sur la machine de reference le 25/09/2026.</summary>
public class PuissancePaquetReaderTests
{
    private sealed class Wmi(Result<IReadOnlyList<WmiRow>> rendu) : IWmiQuery
    {
        public Result<IReadOnlyList<WmiRow>> Query(string wql) => rendu;
        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql) => rendu;
    }

    private static WmiRow Ligne(string nom, ulong mw)
        => new(new Dictionary<string, object?> { ["Name"] = nom, ["Power"] = mw });

    [Fact]
    public void Le_paquet_est_lu_en_watts_et_Total_est_ignore()
    {
        var lignes = new[]
        {
            Ligne("_Total", 0), Ligne("RAPL_Package0_DRAM", 0),
            Ligne("RAPL_Package0_PKG", 47752), Ligne("RAPL_Package0_PP0", 46391),
        };
        PuissancePaquetReader.LireWatts(new Wmi(Result<IReadOnlyList<WmiRow>>.Ok(lignes)))
            .Should().BeApproximately(47.752, 0.0001);
    }

    [Fact]
    public void Sans_compteur_rien_n_est_invente()
    {
        PuissancePaquetReader.LireWatts(new Wmi(Result<IReadOnlyList<WmiRow>>.Ok([]))).Should().BeNull();
        PuissancePaquetReader.LireWatts(new Wmi(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.IoFailure, "x")))
            .Should().BeNull();
    }

    // Un 0 W sur le paquet est un compteur pas encore échantillonné, jamais une mesure : il n'entre pas dans un relevé.
    [Fact]
    public void Zero_watt_sur_le_paquet_est_non_lu()
        => PuissancePaquetReader.LireWatts(new Wmi(Result<IReadOnlyList<WmiRow>>.Ok([Ligne("RAPL_Package0_PKG", 0)])))
            .Should().BeNull();
}
