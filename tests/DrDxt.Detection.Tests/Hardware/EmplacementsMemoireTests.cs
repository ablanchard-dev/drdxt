using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Hardware;

// 06/10/2026 (chantier B1) : deux barrettes en double canal, mais pas dans la paire que les manuels MSI et ASUS
// demandent sur une carte à quatre emplacements (A2 et B2, au bout de la piste). Le risque est la tenue en fréquence, pas
// un chiffre d'images : aucun gain n'est dit, et la carte reste juge (« vérifie le manuel »).
public sealed class EmplacementsMemoireTests
{
    private static MemoryModule M(string emplacement) => new("PN", "G.Skill", 3800, 4000, DeviceLocator: emplacement);

    private static IEnumerable<string> Cles(int emplacements, params string[] noms)
        => MemoryTopologyAdvisor.Advise(noms.Select(M).ToList(), ChassisType.Desktop, emplacements).Select(g => g.MessageKey);

    [Fact]
    public void En_A2_B2_sur_quatre_emplacements_rien_n_est_dit()
        => Cles(4, "Controller0-DIMMA2", "Controller1-DIMMB2").Should().BeEmpty();

    [Theory]
    [InlineData("Controller0-DIMMA1", "Controller1-DIMMB1")]
    [InlineData("DIMM_A1", "DIMM_B1")]
    [InlineData("DIMM_A1", "DIMM_B2")]
    public void Hors_de_la_paire_A2_B2_sur_quatre_emplacements_c_est_dit(string a, string b)
        => Cles(4, a, b).Should().Equal("Finding.Memory.SlotsNotA2B2");

    [Fact]
    public void Sur_une_carte_a_deux_emplacements_A1_B1_est_la_seule_paire()
        => Cles(2, "DIMM_A1", "DIMM_B1").Should().BeEmpty();

    // « ChannelA-DIMM0 » : le canal se lit, la position non (rien ne dit si 0 est au bout de la piste).
    [Fact]
    public void Une_position_que_DrDXT_ne_sait_pas_lire_se_dit_non_lue()
        => Cles(4, "ChannelA-DIMM0", "ChannelB-DIMM0").Should().Equal("Finding.Memory.SlotPositionUnknown");

    // « BANK 0 » : même le canal ne se lit pas ; l'aveu existant suffit, sans en ajouter un second.
    [Fact]
    public void Sans_canal_lisible_un_seul_aveu()
        => Cles(4, "BANK 0", "BANK 2").Should().Equal("Finding.Memory.ChannelTopologyUnknown");

    // Lecture : seuls les tableaux de mémoire système (Use = 3) comptent ; WMI rend des UInt16 et des UInt32.
    [Fact]
    public void Les_emplacements_se_lisent_dans_les_tableaux_de_memoire_systeme()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
        {
            new(new Dictionary<string, object?> { ["Use"] = (ushort)3, ["MemoryDevices"] = (ushort)4 }),
            new(new Dictionary<string, object?> { ["Use"] = (ushort)7, ["MemoryDevices"] = (ushort)2 }),
        }));
        new MemoryDetector(wmi).Emplacements().Should().Be(4);
    }

    [Fact]
    public void Une_lecture_ratee_rend_zero_jamais_un_nombre_devine()
    {
        var wmi = Substitute.For<IWmiQuery>();
        wmi.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "non lu"));
        new MemoryDetector(wmi).Emplacements().Should().Be(0);
    }

    [Fact]
    public void Nombre_d_emplacements_non_lu_rien_n_est_dit_sur_la_position()
        => Cles(0, "DIMM_A1", "DIMM_B1").Should().BeEmpty();
}
