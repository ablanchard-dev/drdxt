using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// Le bilan santé, huit lignes fixes, chacune un constat. Une ligne sans constat se dit
// non lue, jamais « bonne ».
public sealed class BilanSanteTests
{
    private static RepairFinding F(string domaine, string nom, string? valeur, RepairStatus statut = RepairStatus.Observed)
        => new(domaine, nom, valeur, null, statut, "explication");

    [Fact]
    public void Le_bilan_a_toujours_huit_lignes_dans_le_meme_ordre()
        => BilanSante.Construire([]).Select(l => l.Titre).Should().Equal(
            "Température du processeur", "Température de la carte graphique", "Disques", "Arrêts brutaux",
            "Erreurs matérielles", "Mises à jour Windows", "Pilote graphique", "Point de restauration conseillé");

    [Fact]
    public void Une_ligne_sans_constat_est_non_lue_sauf_le_point_sain_sans_souci()
    {
        var bilan = BilanSante.Construire([]);

        bilan.Take(7).Should().OnlyContain(l => l.Constat.Etat == EtatConstat.NonLu);
        bilan[7].Constat.Etat.Should().Be(EtatConstat.SansObjet, "sans souci en cours, aucun point n'est à préférer");
    }

    [Fact]
    public void Chaque_ligne_prend_son_constat()
    {
        var bilan = BilanSante.Construire([
            F("THERMIQUE", "temperature du processeur", "56 °C"),
            F("THERMIQUE", "temperature du GPU", "58 °C"),
            F("STABILITE", "arrets brutaux", "11 sur 90 jours", RepairStatus.Suspicious),
            F("STABILITE", "point de restauration sain", "aucun point d'avant le souci"),
        ]);

        bilan[0].Constat.Valeur.Should().Be("56 °C");
        bilan[1].Constat.Valeur.Should().Be("58 °C");
        bilan[3].Constat.Gravite.Should().Be(Severity.Minor);
        bilan[7].Constat.Valeur.Should().Be("aucun point d'avant le souci");
    }

    [Fact]
    public void Les_mises_a_jour_montrent_d_abord_ce_qui_attend()
        => BilanSante.Construire([
            F("WINDOWS UPDATE", "echecs", "31 échecs en 90 jours"),
            F("WINDOWS UPDATE", "en attente", "4"),
        ])[5].Constat.Valeur.Should().Be("4");

    [Fact]
    public void Les_disques_montrent_le_constat_le_plus_grave()
    {
        var bilan = BilanSante.Construire([
            F("STOCKAGE", "usure · Samsung SSD 990 PRO 2TB", "0 %"),
            F("STOCKAGE", "erreurs non corrigées · Samsung SSD 990 PRO 2TB", "3", RepairStatus.Suspicious),
            F("STOCKAGE", "place libre du disque système", "C : 297 Go libres"),
        ]);

        bilan[2].Constat.Valeur.Should().Be("3");
    }

    // À gravité égale, un constat lu passait devant un non lu ; un second disque
    // illisible disparaissait derrière « usure 0 % », une liste de Windows Update non lue derrière « aucun échec ».
    [Fact]
    public void A_gravite_egale_le_non_lu_passe_devant_le_lu()
    {
        var bilan = BilanSante.Construire([
            F("STOCKAGE", "usure · SSD A", "0 %"),
            F("STOCKAGE", "usure · SSD B", null, RepairStatus.Unknown),
            F("WINDOWS UPDATE", "echecs", "aucun", RepairStatus.ExpectedDefault),
            F("WINDOWS UPDATE", "en attente", null, RepairStatus.Unknown),
        ]);

        bilan[2].Constat.Etat.Should().Be(EtatConstat.NonLu);
        bilan[5].Constat.Etat.Should().Be(EtatConstat.NonLu);
    }

    // La ligne « Pilote graphique » ne lisait pas la version du pilote (« version du pilote graphique »).
    [Fact]
    public void La_ligne_du_pilote_lit_aussi_sa_version()
    {
        var bilan = BilanSante.Construire([
            F("GPU DRIVER", "pilotes d'affichage installés", "NVIDIA 32.0.16.1714", RepairStatus.ExpectedDefault),
            F("GPU DRIVER", "version du pilote graphique", "NVIDIA 617.14", RepairStatus.Suspicious),
        ]);

        bilan[6].Constat.Valeur.Should().Be("NVIDIA 617.14");
    }
}
