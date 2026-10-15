using System.IO;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// « trop de pilotes ». Les sorties réelles de pnputil sur la machine (un seul paquet NVIDIA en service,
// aucune carte déconnectée), puis la même forme avec deux anciens paquets et une ancienne carte ; le paquet en service est
// au milieu de la liste, pour qu'aucune règle de position ne passe pour la bonne.
public sealed class MagasinDePilotesTests
{
    private static string Fichier(string nom)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "tests", "DrDxt.Detection.Tests", "Gpu", nom));
    }

    [Fact]
    public void La_machine_reelle_n_a_pas_de_doublon()
    {
        var c = MagasinDePilotes.Juger(Fichier("pnputil-pilotes-2026-10-03.xml"), Fichier("pnputil-cartes-2026-10-03.xml"),
            Fichier("pnputil-deconnectees-2026-10-03.xml")).ToList();
        c.Should().ContainSingle();
        c[0].Statut.Should().Be(RepairStatus.ExpectedDefault);
        c[0].ValeurCourante.Should().Be("NVIDIA 32.0.16.1656 (oem127.inf)");
    }

    private const string Trois = """
        <PnpUtil>
          <Driver DriverName="oem98.inf"><ProviderName>NVIDIA</ProviderName><DriverVersion>05/02/2026 32.0.15.9186</DriverVersion></Driver>
          <Driver DriverName="oem127.inf"><ProviderName>NVIDIA</ProviderName><DriverVersion>08/20/2026 32.0.16.1656</DriverVersion></Driver>
          <Driver DriverName="oem41.inf"><ProviderName>Advanced Micro Devices, Inc.</ProviderName><DriverVersion>01/10/2025 31.0.24027.1012</DriverVersion></Driver>
        </PnpUtil>
        """;

    private const string UneAncienneCarte = """
        <PnpUtil><Device InstanceId="PCI\VEN_1002"><DeviceDescription>AMD Radeon RX 6600</DeviceDescription><DriverName>oem41.inf</DriverName></Device></PnpUtil>
        """;

    [Fact]
    public void Les_anciens_paquets_et_l_ancienne_carte_sont_nommes_avec_la_commande()
    {
        var c = MagasinDePilotes.Juger(Trois, Fichier("pnputil-cartes-2026-10-03.xml"), UneAncienneCarte).ToList();
        c.Should().HaveCount(2);
        c[0].Statut.Should().Be(RepairStatus.Suspicious);
        c[0].ValeurCourante.Should().Be("NVIDIA 32.0.15.9186 (oem98.inf), Advanced Micro Devices, Inc. 31.0.24027.1012 (oem41.inf)",
            "sous le titre « anciens pilotes », le paquet en service n'a pas sa place");
        c[0].Explication.Should().Contain("2 paquet(s)").And.NotContain("pnputil")
            .And.NotContain("oem127.inf)", "le paquet en service n'est jamais proposé au retrait")
            .And.Contain("ne les retire pas");
        c[0].Remede.Should().Contain("pnputil /delete-driver oem98.inf puis pnputil /delete-driver oem41.inf")
            .And.NotContain("oem127.inf", "le paquet en service n'est jamais proposé au retrait");
        c[1].Explication.Should().Contain("AMD Radeon RX 6600");
        c[1].Remede.Should().Contain("Afficher les périphériques cachés").And.Contain("AMD Radeon RX 6600");
        c[0].Remede.Should().StartWith("1) Retire d'abord les cartes déconnectées", "un paquet lié à une carte absente refuse de partir");
        c[0].Cible.Should().Be("oem98.inf oem41.inf", "les paquets que le geste retire, et seulement eux");
    }

    // Relevé sur la machine : pnputil écrit dans la page de code OEM ; lu en UTF-8, « carte mère » sortait abîmé.
    [Fact]
    public void Le_vrai_pnputil_se_lit_sans_caractere_abime()
    {
        var sortie = MagasinDePilotes.Pnputil("/enum-devices");
        if (sortie.Length == 0) return; // pas de pnputil sur cette machine
        sortie.Should().NotContain("�").And.Contain("<PnpUtil");
    }

    // Une carte virtuelle déconnectée (Bureau à distance, Parsec) n'est pas une ancienne carte.
    [Fact]
    public void Une_carte_virtuelle_deconnectee_n_est_pas_un_reste()
    {
        const string virtuelles = """
            <PnpUtil>
              <Device InstanceId="SWD\REMOTEDISPLAYENUM\RDPIDDINDIRECTDISPLAY"><DeviceDescription>Microsoft Remote Display Adapter</DeviceDescription></Device>
              <Device InstanceId="ROOT\DISPLAY\0000"><DeviceDescription>Parsec Virtual Display Adapter</DeviceDescription></Device>
            </PnpUtil>
            """;
        MagasinDePilotes.Juger(Fichier("pnputil-pilotes-2026-10-03.xml"), Fichier("pnputil-cartes-2026-10-03.xml"), virtuelles)
            .Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Une_sortie_illisible_ne_dit_rien() => MagasinDePilotes.Juger("pas du xml", "", "").Should().BeEmpty();

    // Le vrai pnputil de la machine : la lecture en direct rend le même verdict que les sorties enregistrées.
    [Fact, Trait("Category", "Machine")]
    public void Le_vrai_pnputil_rend_un_xml_lisible()
    {
        var pilotes = MagasinDePilotes.Pnputil("/enum-drivers /class Display");
        if (pilotes.Length == 0) return; // pnputil absent : rien à vérifier
        MagasinDePilotes.Paquets(pilotes).Should().NotBeEmpty("au moins le pilote d'affichage en service");
    }

    // pnputil rend "" sur toute erreur : sans cartes lues, tous les paquets passaient pour « inutiles », celui en
    // service compris, avec sa commande de suppression. Cartes illisibles = non jugé.
    [Fact]
    public void Sans_cartes_lues_aucun_paquet_n_est_dit_inutile()
    {
        var c = MagasinDePilotes.Juger(Fichier("pnputil-pilotes-2026-10-03.xml"), "", "").ToList();

        c.Should().NotContain(x => x.Statut == RepairStatus.Suspicious);
        c.Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
    }

    // Deux cartes branchées, une seule a dit son pilote : celui de l'autre est peut-être un des paquets, il n'est pas dit inutile.
    [Fact]
    public void Une_carte_dont_le_pilote_n_est_pas_lu_suffit_a_ne_rien_juger()
    {
        const string deux = """
            <PnpUtil>
              <Device InstanceId="PCI\VEN_10DE"><DeviceDescription>NVIDIA GeForce RTX 4060</DeviceDescription><DriverName>oem127.inf</DriverName></Device>
              <Device InstanceId="PCI\VEN_8086"><DeviceDescription>Intel UHD Graphics 770</DeviceDescription></Device>
            </PnpUtil>
            """;

        var c = MagasinDePilotes.Juger(Trois, deux, "").ToList();

        c.Should().NotContain(x => x.Statut == RepairStatus.Suspicious);
        c.Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
    }
}
