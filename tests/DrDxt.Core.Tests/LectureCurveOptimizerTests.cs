using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le Curve Optimizer lu dans le BIOS, le SMU en simple témoin, et le conseil qui ne pousse plus une puce déjà poussée.
public sealed class LectureCurveOptimizerTests
{
    private static BiosSettingReading Lu(string nom, string libelle, long brut = 0) => new(nom, brut, libelle, false);

    private static IReadOnlyList<BiosSettingReading> TousCoeurs(string signe, long amplitude, string echelle = "Auto") =>
        [Lu("Curve Optimizer", "All Cores"), Lu("Curve Optimizer Sign", signe), Lu("Curve Optimizer Magnitude", amplitude.ToString(), amplitude),
         Lu("PBO Scalar", echelle)];

    [Fact]
    public void Le_mode_tous_les_coeurs_donne_signe_et_amplitude()
    {
        LectureCurveOptimizer.DepuisBios(TousCoeurs("Negative", 20, "4X"))
            .Should().Be(new CurveOptimizerLu(true, "All Cores", -20, 4.0));
    }

    [Fact]
    public void Aucune_borne_a_30_l_amplitude_est_celle_du_menu()
    {
        // Zen 4 va au-delà de 30 : borner à 30 lirait faux une valeur que la carte accepte.
        LectureCurveOptimizer.DepuisBios(TousCoeurs("Negative", 45))!.DecalageTousCoeurs.Should().Be(-45);
    }

    [Fact]
    public void Desactive_cœur_par_cœur_ou_incomplet()
    {
        LectureCurveOptimizer.DepuisBios([Lu("Curve Optimizer", "Disabled")])!.Actif.Should().BeFalse();
        LectureCurveOptimizer.DepuisBios([Lu("Curve Optimizer", "Per Core")]).Should().Be(new CurveOptimizerLu(true, "Per Core", null, null));
        LectureCurveOptimizer.DepuisBios([Lu("Curve Optimizer", "All Cores")]).Should().BeNull("sans signe ni amplitude");
        LectureCurveOptimizer.DepuisBios([]).Should().BeNull();
        LectureCurveOptimizer.DepuisBios(null).Should().BeNull();
    }

    [Fact]
    public void Le_temoin_SMU_qui_contredit_le_BIOS_rend_non_lu()
    {
        var bios = LectureCurveOptimizer.DepuisBios(TousCoeurs("Negative", 20, "4X"));

        LectureCurveOptimizer.Croiser(bios, 4.0, [-20, -20]).Should().Be(bios);
        LectureCurveOptimizer.Croiser(bios, null, null).Should().Be(bios);
        LectureCurveOptimizer.Croiser(bios, 1.0, null).Should().BeNull("échelle différente");
        LectureCurveOptimizer.Croiser(bios, null, [-20, -10]).Should().BeNull("un cœur ne tourne pas comme le BIOS le dit");
        LectureCurveOptimizer.Croiser(LectureCurveOptimizer.DepuisBios([Lu("Curve Optimizer", "Disabled")]), null, [-5]).Should().BeNull();
        LectureCurveOptimizer.Croiser(null, 4.0, [-20]).Should().BeNull("le SMU seul n'est qu'un témoin");
    }

    [Fact]
    public void L_echelle_du_SMU_est_un_flottant_borne()
    {
        LectureCurveOptimizer.EchelleSmu((uint)BitConverter.SingleToInt32Bits(4f)).Should().Be(4.0);
        LectureCurveOptimizer.EchelleSmu((uint)BitConverter.SingleToInt32Bits(11f)).Should().BeNull();
        LectureCurveOptimizer.EchelleSmu((uint)BitConverter.SingleToInt32Bits(float.NaN)).Should().BeNull();
    }

    [Fact]
    public void Un_Curve_Optimizer_deja_actif_n_est_plus_conseille_et_sa_valeur_est_dite()
    {
        var lu = LectureCurveOptimizer.DepuisBios(TousCoeurs("Negative", 20));

        var c = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.", [], DateTimeOffset.UtcNow, null, lu)!;

        c.Action.Should().Be(RepairAction.Aucune);
        c.ValeurCourante.Should().Be("All Cores -20");
        c.Explication.Should().Be(Docteur.T("CO.DejaActif", "AMD Ryzen 7 7800X3D 8-Core Processor", "All Cores -20"));
        CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.", [], DateTimeOffset.UtcNow, null,
            LectureCurveOptimizer.DepuisBios([Lu("Curve Optimizer", "Disabled")]))!.Action.Should().Be(RepairAction.Recommandation);
    }
}
