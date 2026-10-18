using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Une règle d'installation interdite est rapportée à la carte qu'elle vise vraiment, jamais à une autre.
public sealed class InstallationDePiloteTests
{
    private const string InstanceRtx = @"PCI\VEN_10DE&DEV_2882&SUBSYS_51611462&REV_A1\4&22A14BB8&0&0008";

    private static readonly InstallationDePilote.Carte Rtx = new("NVIDIA GeForce RTX 4060",
        [@"PCI\VEN_10DE&DEV_2882&SUBSYS_51611462", @"PCI\VEN_10DE&DEV_2882", @"PCI\CC_0300"], InstanceRtx);
    private static readonly InstallationDePilote.Carte Igpu = new("AMD Radeon(TM) Graphics", [@"PCI\VEN_1002&DEV_164E", @"PCI\CC_0300"]);

    private static readonly string[] Classe = [InstallationDePilote.ClasseAffichage];

    private static InstallationDePilote.Regles Regles(string[]? ids = null, bool classe = false, bool wu = false, bool couches = false,
        string[]? instances = null, string[]? instancesOk = null, string[]? idsOk = null, string[]? classesOk = null, bool nonDecrits = false)
        => new(couches, instances ?? [], instancesOk ?? [], ids ?? [], idsOk ?? [], classe ? Classe : [], classesOk ?? [], nonDecrits, false, wu);

    [Fact]
    public void Une_regle_sur_la_puce_integree_nomme_la_puce_et_pas_la_carte_dediee()
    {
        var c = InstallationDePilote.Juger(Regles([@"pci\ven_1002&dev_164e"]), [Rtx, Igpu]).Single();

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Contain("AMD Radeon").And.NotContain("RTX");
    }

    [Fact]
    public void La_classe_entiere_vise_chaque_carte_et_les_non_decrits_celles_qu_aucune_regle_n_autorise()
    {
        InstallationDePilote.Juger(Regles(classe: true), [Rtx, Igpu]).Single().ValeurCourante.Should().Contain("RTX").And.Contain("Radeon");
        // DenyUnspecified : seule une autorisation sauve la carte.
        InstallationDePilote.Juger(Regles(nonDecrits: true, idsOk: [@"PCI\VEN_10DE&DEV_2882"]), [Rtx, Igpu]).Single()
            .ValeurCourante.Should().Contain("Radeon").And.NotContain("RTX");
        InstallationDePilote.Juger(Regles(nonDecrits: true, classesOk: Classe), [Rtx]).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Un_identifiant_d_instance_interdit_bloque_la_carte()
        => InstallationDePilote.Juger(Regles(instances: [InstanceRtx.ToLowerInvariant()]), [Rtx, Igpu]).Single()
            .ValeurCourante.Should().Contain("RTX").And.NotContain("Radeon");

    [Fact]
    public void Sans_ordre_en_couches_un_refus_l_emporte_sur_toute_autorisation()
    {
        // Microsoft Learn : par défaut, toute stratégie « Empêcher » a priorité sur celles qui autorisent.
        InstallationDePilote.Juger(Regles(classe: true, idsOk: [@"PCI\VEN_10DE&DEV_2882"]), [Rtx]).Single()
            .Statut.Should().Be(RepairStatus.Suspicious);
        InstallationDePilote.Juger(Regles([@"PCI\VEN_10DE&DEV_2882"], instancesOk: [InstanceRtx]), [Rtx]).Single()
            .Statut.Should().Be(RepairStatus.Suspicious);
    }

    [Fact]
    public void En_couches_le_critere_le_plus_precis_l_emporte()
    {
        // AllowDenyLayered : instance > identifiant > classe ; dans une couche, le refus passe avant l'autorisation.
        var c = InstallationDePilote.Juger(Regles(classe: true, idsOk: [@"PCI\VEN_10DE&DEV_2882"], couches: true), [Rtx, Igpu]).Single();
        c.ValeurCourante.Should().Contain("Radeon").And.NotContain("RTX");
        InstallationDePilote.Juger(Regles([@"PCI\VEN_10DE&DEV_2882"], instancesOk: [InstanceRtx], couches: true), [Rtx]).Single()
            .Statut.Should().NotBe(RepairStatus.Suspicious);
        InstallationDePilote.Juger(Regles([@"PCI\VEN_10DE&DEV_2882"], idsOk: [@"PCI\VEN_10DE&DEV_2882"], couches: true), [Rtx]).Single()
            .Statut.Should().Be(RepairStatus.Suspicious, "même couche : le refus d'abord");
        // En couches, « non décrits » est ignoré.
        InstallationDePilote.Juger(Regles(nonDecrits: true, couches: true), [Rtx]).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Une_regle_sur_un_autre_materiel_est_dite_sans_alarme_et_rien_n_est_lu_sans_lecture()
    {
        InstallationDePilote.Juger(Regles([@"USB\VID_1234&PID_5678"]), [Rtx]).Single().Statut.Should().Be(RepairStatus.Observed);
        InstallationDePilote.Juger(Regles(), [Rtx]).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);
        InstallationDePilote.Juger(null, [Rtx]).Single().Statut.Should().Be(RepairStatus.Unknown);
        InstallationDePilote.Juger(Regles(), null).Single().Statut.Should().Be(RepairStatus.Unknown);
        InstallationDePilote.Juger(Regles(wu: true), [Rtx]).Should().Contain(c => c.Nom == "pilotes exclus de Windows Update");
    }

    // Le lecteur ne fait qu'ouvrir des clés : aucune écriture de registre dans ce fichier.
    [Fact]
    public void Le_lecteur_ne_touche_jamais_a_la_strategie()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.Detection", "Repair", "InstallationDePilote.cs"))
            .Should().NotContain("SetValue").And.NotContain("DeleteValue").And.NotContain("CreateSubKey").And.NotContain("writable: true");
    }
}
