using System.Globalization;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Deux valeurs s'affichaient telles que Windows ou le BIOS les écrivent. La fréquence mémoire du BIOS se dit en MT/s et en
/// Gear, le nom exact de l'option restant en tête de ligne pour la retrouver dans le BIOS. L'incident noyau du pilote
/// graphique se dit en mots, le code d'arrêt gardé entre parenthèses pour qui le cherche.
/// </summary>
public sealed class LeJargonBrutSeDitEnClairTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    [Fact]
    public void La_frequence_memoire_du_bios_se_dit_en_mts_et_en_gear()
    {
        using var _ = Docteur.Portee(En);
        var ligne = CarteDeLaMachine.LignesBios([new BiosSettingReading("DRAM Frequency", 30, "3800 G1 (38x1.00x100.00)", IsDefault: false)]).Single();

        ligne.Should().StartWith("DRAM Frequency", "le nom exact de l'option reste, pour la retrouver dans le BIOS");
        ligne.Should().Contain("3800 MT/s, Gear 1").And.NotContain("38x1.00x100.00");
    }

    [Fact]
    public void Un_libelle_bios_qui_n_est_pas_une_frequence_reste_tel_quel()
    {
        using var _ = Docteur.Portee(En);
        CarteDeLaMachine.LignesBios([new BiosSettingReading("Extreme Memory Profile(X.M.P)", 1, "Profile 1", IsDefault: false)])
            .Single().Should().Contain("Profile 1");
    }

    [Fact]
    public void L_incident_du_pilote_graphique_se_dit_en_mots_avec_son_code()
    {
        var incident = new RepairFinding("STABILITE", "incident noyau conserve", "3x 0x193 VIDEO_DXGKRNL_LIVEDUMP (paramètre 1 : 0x80e)",
            null, RepairStatus.Suspicious, "explication", RepairAction.Aucune);

        using (Docteur.Portee(Fr))
            RepairFindingViewModel.ValeurAffichee(incident).Should().Contain("rapport du pilote graphique").And.Contain("0x193")
                .And.NotContain("VIDEO_DXGKRNL_LIVEDUMP");
        using (Docteur.Portee(En))
            RepairFindingViewModel.ValeurAffichee(incident).Should().Contain("graphics driver report").And.Contain("0x193");
    }
}
