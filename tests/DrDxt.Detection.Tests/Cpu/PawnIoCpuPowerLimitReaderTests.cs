using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// Le câblage des gardes du lecteur de limites, prouvé sans la machine. Registres de la machine
// de référence : PL1 = PL2 = 253 W (2024 huitièmes), puissance de base 65 W (520 huitièmes), unité 1/8 W.
public sealed class PawnIoCpuPowerLimitReaderTests
{
    private static readonly RegistresCpuLus Reference = new(0x3, (2024UL << 32) | (1UL << 47) | (1UL << 15) | 2024UL, 520UL);
    private const string RaptorLake = "Intel64 Family 6 Model 183 Stepping 1";

    private static PawnIoCpuPowerLimitReader Lecteur(RegistresCpuLus? direct = null, EtatCpuServi? servi = null,
        uint revision = 0x133, IReadOnlyList<ChaleurEnPartie.PartieLue>? parties = null, IReadOnlyList<TweakSnapshot>? journal = null)
        => new(() => (direct, 0UL), () => servi, () => parties ?? [], () => ("Intel(R) Core(TM) i7-14700F", revision, RaptorLake),
            () => journal ?? []);

    [Fact]
    public void Sans_droits_les_registres_du_service_sont_lus()
    {
        var r = Lecteur(servi: new EtatCpuServi(DateTimeOffset.Now, null, Reference, 0UL)).LireRegistres();
        r.IsOk.Should().BeTrue();
        r.Value!.Registres.Should().Be(Reference);
        r.Value.Lue.Pl1W.Should().Be(253);
        Lecteur().LireRegistres().IsOk.Should().BeFalse("ni direct ni service : non lu, jamais un chiffre");
    }

    [Fact]
    public void La_garde_microcode_est_cablee_dans_la_lecture()
    {
        Lecteur(direct: Reference, revision: 0x12B).Lire()!.MicrocodeVminPerime.Should().BeTrue("0x12B est d'avant le correctif 0x12F");
        Lecteur(direct: Reference, revision: 0x133).Lire()!.MicrocodeVminPerime.Should().BeFalse("relevé sur la machine de référence");
    }

    [Fact]
    public void Le_plafond_thermique_vient_des_parties_lues()
    {
        var chaude = new ChaleurEnPartie.PartieLue("cod.exe", DateTimeOffset.Now, 100, 110, 25, new FreinageCpu(600, 120, 0, 0, 0, 92, 100, 90));
        Lecteur(direct: Reference, parties: [chaude]).Lire()!.PlafondThermiqueW.Should().Be(150, "110 W médians × 1,35, à la dizaine");
        Lecteur(direct: Reference).Lire()!.PlafondThermiqueW.Should().BeNull();
    }

    // Quand le plafond marche, la partie freinée qui l'a demandé sort de la fenêtre du docteur (5 parties,
    // 14 jours) ; le plafond lu repassait à null, la montée revenait par-dessus, la chaleur aussi : une oscillation. Tant que le
    // plafond est ENCORE POSÉ dans le registre, sa valeur reste lue ; au redémarrage (le BIOS remet la sienne), elle s'efface.
    [Fact]
    public void Un_plafond_encore_pose_reste_lu_quand_la_partie_qui_l_a_demande_sort_de_la_fenetre()
    {
        var plafonne = Reference.PkgPowerLimit & ~0x7FFFUL | 1280UL; // PL1 160 W, PL2 253 W
        var pose = new TweakSnapshot("fixit.cpu_power_cap",
            PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(Reference.PkgPowerLimit, plafonne)).Value!,
            PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(plafonne, Reference.PkgPowerLimit)).Value!);

        var lu = Lecteur(direct: Reference with { PkgPowerLimit = plafonne }, journal: [pose]).Lire()!;
        lu.PlafondThermiqueW.Should().Be(160);
        LimiteDePuissance.CibleDeMontee(lu).Should().BeNull("la montée reste en retrait tant que le plafond tient");

        Lecteur(direct: Reference, journal: [pose]).Lire()!.PlafondThermiqueW.Should().BeNull("redémarré : le BIOS a remis 253 W");
    }
}
