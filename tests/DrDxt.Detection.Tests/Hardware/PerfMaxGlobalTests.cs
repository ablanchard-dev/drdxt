using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// 24/09/2026, machine de référence, PC inactif : 2 490 MHz, mémoire vidéo au maximum, 55,6 W, raison
/// publiée « GPU inactif ». Cause lue dans le pilote : « performances maximales » au profil GLOBAL.
/// Le constat ne vise que le global : au profil du jeu, c'est le levier de DrDXT, et c'est voulu.
/// </summary>
public sealed class PerfMaxGlobalTests
{
    private const string Cle = "Finding.Gpu.PerfMaxGlobal";

    private static HardwareInventory Machine(ChassisType chassis = ChassisType.Desktop) => new(
        new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        Array.Empty<GpuInfo>(),
        Array.Empty<MemoryModule>(),
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.H0", DateTimeOffset.UnixEpoch, true),
        chassis);

    private static SettingsState Etat(TriState perfMax, string? scope) => new(
        TriState.Unknown, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown)
    { NvidiaPreferMaxPerf = perfMax, NvidiaDrsScope = scope };

    [Fact]
    public void Au_profil_global_le_constat_est_la()
    {
        var f = ScoringRules.Evaluate(Machine(), Etat(TriState.On, null)).Single(x => x.TitleKey == Cle);
        f.Severity.Should().Be(Severity.Minor);
        f.Category.Should().Be("Gpu");
    }

    [Theory]
    [InlineData(TriState.On, @"C:\Games\cod.exe")]   // posé au profil du jeu : le levier de DrDXT, voulu
    [InlineData(TriState.Off, null)]
    [InlineData(TriState.Unknown, null)]              // non lu : rien d'affirmé
    public void Hors_du_global_ou_non_lu_rien_n_est_dit(TriState perfMax, string? scope)
        => ScoringRules.Evaluate(Machine(), Etat(perfMax, scope)).Should().NotContain(x => x.TitleKey == Cle);

    [Fact]
    public void Au_global_le_conseil_des_frequences_d_ecran_se_tait()
    {
        var etat = Etat(TriState.On, null) with { AttachedRefreshHz = new[] { 200, 144 } };
        ScoringRules.Evaluate(Machine(), etat).Should().NotContain(x => x.Category == "Display");

        var auJeu = Etat(TriState.On, @"C:\Games\cod.exe") with { AttachedRefreshHz = new[] { 200, 144 } };
        ScoringRules.Evaluate(Machine(), auJeu).Should().Contain(x => x.Category == "Display",
            "hors du global, les fréquences mêlées restent la piste à essayer");
    }

    [Fact]
    public void Un_portable_n_est_pas_vise()
        => ScoringRules.Evaluate(Machine(ChassisType.Laptop), Etat(TriState.On, null))
            .Should().NotContain(x => x.TitleKey == Cle);
}
