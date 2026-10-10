using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>B1 (10/10/2026) : l'écriture de la limite de puissance du processeur relit avant (le monde n'a pas bougé,
/// pas de verrou) et après (le registre porte ce qui a été écrit) ; sinon elle dit pourquoi, jamais un succès sur parole.</summary>
public sealed class PawnIoCpuPowerActionsTests
{
    private const ulong Origine = (1752UL << 32) | (1UL << 47) | (1UL << 15) | 520UL; // PL2 219 W, PL1 65 W
    private static readonly ulong Cible = RegistreDeLimiteCpu.ComposerPl1(Origine, 219, 0x3);

    private static PawnIoIntel.RegistresDeLimite Registres(ulong limite) => new(0x3, limite, 520UL);

    [Fact]
    public void Ecrit_quand_le_registre_porte_encore_l_origine_et_que_la_relecture_confirme()
    {
        var ecrites = new List<ulong>();
        var a = new PawnIoCpuPowerActions(() => Registres(Origine), v => { ecrites.Add(v); return true; });

        a.SetPackagePowerLimit(Cible, Origine).IsOk.Should().BeTrue();
        ecrites.Should().Equal(Cible);
    }

    [Fact]
    public void Deja_a_la_valeur_demandee_rien_n_est_ecrit_et_c_est_un_succes()
    {
        var ecrites = 0;
        var a = new PawnIoCpuPowerActions(() => Registres(Cible), _ => { ecrites++; return true; });

        a.SetPackagePowerLimit(Cible, Origine).IsOk.Should().BeTrue();
        ecrites.Should().Be(0);
    }

    [Fact]
    public void Le_registre_a_bouge_depuis_la_lecture_rien_n_est_ecrit()
    {
        var ecrites = 0;
        var a = new PawnIoCpuPowerActions(() => Registres(Origine | 7UL), _ => { ecrites++; return true; });

        var r = a.SetPackagePowerLimit(Cible, Origine);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("changed");
        ecrites.Should().Be(0);
    }

    [Fact]
    public void Un_verrou_du_BIOS_est_dit_et_rien_n_est_ecrit()
    {
        var ecrites = 0;
        var a = new PawnIoCpuPowerActions(() => Registres(Origine | (1UL << 63)), _ => { ecrites++; return true; });

        var r = a.SetPackagePowerLimit(Cible, Origine | (1UL << 63));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("locked");
        ecrites.Should().Be(0);
    }

    [Fact]
    public void Une_ecriture_non_relue_est_un_echec_dit()
    {
        var a = new PawnIoCpuPowerActions(() => Registres(Origine), _ => false);

        var r = a.SetPackagePowerLimit(Cible, Origine);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("not read back");
    }

    [Fact]
    public void Sans_registre_lisible_rien_n_est_ecrit()
    {
        var ecrites = 0;
        var a = new PawnIoCpuPowerActions(() => null, _ => { ecrites++; return true; });

        a.SetPackagePowerLimit(Cible, Origine).IsOk.Should().BeFalse();
        ecrites.Should().Be(0);
    }
}
