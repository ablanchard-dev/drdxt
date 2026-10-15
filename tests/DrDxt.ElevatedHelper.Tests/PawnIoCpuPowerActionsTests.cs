using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>L'écriture de la limite de puissance du processeur relit avant (le monde n'a pas bougé,
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

    // La garde Vmin vit aussi dans le helper. Monter la PL1 d'un Core 13e/14e au microcode d'avant le
    // correctif est refusé ici, quel que soit l'appelant ; baisser (plafond, retour d'une montée) reste permis.
    [Fact]
    public void Sur_un_microcode_perime_le_helper_refuse_de_monter_mais_laisse_baisser()
    {
        var ecrites = new List<ulong>();
        var monter = new PawnIoCpuPowerActions(() => Registres(Origine), v => { ecrites.Add(v); return true; }, microcodePerime: () => true);
        var r = monter.SetPackagePowerLimit(Cible, Origine);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("microcode");
        ecrites.Should().BeEmpty();

        var baisser = new PawnIoCpuPowerActions(() => Registres(Cible), v => { ecrites.Add(v); return true; }, microcodePerime: () => true);
        baisser.SetPackagePowerLimit(Origine, Cible).IsOk.Should().BeTrue();
        ecrites.Should().Equal(Origine);
    }

    // Annuler un plafond REMONTE la PL1 vers la valeur du firmware ; la garde Vmin la refusait comme une
    // montée, et le plafond restait posé jusqu'au redémarrage. Revenir à la valeur que la carte tenait à ce démarrage ne dépasse
    // jamais ce qu'elle avait réglé : permis. Monter au-delà reste refusé.
    [Fact]
    public void Sur_un_microcode_perime_annuler_un_plafond_rend_la_valeur_du_firmware_mais_jamais_au_dela()
    {
        ulong? retenue = null;
        Func<ulong, ulong> firmware = courant => retenue ??= courant; // le premier registre vu à ce démarrage
        var registre = Cible;                                          // le firmware tient PL1 = 219 W
        var ecrites = new List<ulong>();
        PawnIoCpuPowerActions Actions() => new(() => Registres(registre), v => { ecrites.Add(v); registre = v; return true; },
            microcodePerime: () => true, firmwareDuDemarrage: firmware);

        Actions().SetPackagePowerLimit(Origine, Cible).IsOk.Should().BeTrue("poser le plafond : une baisse");
        Actions().SetPackagePowerLimit(Cible, Origine).IsOk.Should().BeTrue("l'annuler : retour à la valeur du firmware");
        ecrites.Should().Equal(Origine, Cible);

        retenue = Origine; registre = Origine;                         // firmware à 65 W : 219 serait au-delà
        var r = Actions().SetPackagePowerLimit(Cible, Origine);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("microcode");
    }

    // Le plancher de la PL1 (jamais sous le cinquième de la PL2) vit ici, à côté de l'origine du
    // démarrage : une PL1 d'usine déjà sous ce plancher (portable OEM, 25 W sous une PL2 de 157 W) se rend toujours ; une baisse
    // arbitraire sous le plancher est refusée.
    [Fact]
    public void Le_plancher_refuse_une_baisse_arbitraire_mais_rend_toujours_la_PL1_d_usine()
    {
        const ulong Usine = (1256UL << 32) | (1UL << 47) | (1UL << 15) | 200UL; // PL2 157 W, PL1 25 W
        var monte = RegistreDeLimiteCpu.ComposerPl1(Usine, 65, 0x3);
        var registre = monte;
        var ecrites = new List<ulong>();
        var a = new PawnIoCpuPowerActions(() => Registres(registre), v => { ecrites.Add(v); registre = v; return true; },
            firmwareDuDemarrage: _ => Usine);

        a.SetPackagePowerLimit(Usine, monte).IsOk.Should().BeTrue("rendre la PL1 d'usine, même sous le plancher");
        var bas = Usine & ~0x7FFFUL | 8UL; // 1 W
        a.SetPackagePowerLimit(bas, Usine).IsOk.Should().BeFalse("1 W : une baisse arbitraire sous le cinquième de la PL2");
        ecrites.Should().Equal(Usine);
    }
}
