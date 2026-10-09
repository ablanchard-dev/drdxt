using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Repair;

public sealed class RepairLibellesTests
{
    [Fact]
    public void Un_nom_de_registre_brut_s_affiche_en_mots_sans_changer_sa_cle()
        // La clé reste celle du journal (deux photos se comparent par Domaine + Nom) ; seul l'écran traduit.
        => RepairLibelles.Nom("Valeur de registre NVTweak LowLatencyMode")
            .Should().Be("Faible latence NVIDIA posée dans le registre (sans effet prouvé)");

    // 25/09/2026 : DrDXT lit l'écran PRINCIPAL. L'appeler « écran de jeu » supposait que le jeu y tourne,
    // ce qu'il ne sait pas sur une machine à deux écrans (celle de la mise au point en a deux, 200 et 144 Hz).
    [Fact]
    public void La_frequence_lue_est_celle_de_l_ecran_principal_pas_d_un_ecran_de_jeu_suppose()
        => RepairLibelles.Nom("frequence").Should().Be("Fréquence de l'écran principal");
}
