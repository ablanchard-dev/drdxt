using System.IO;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// « si je télécharge un truc qui casse tout, DrDXT doit le voir ». Ce qui est apparu, a disparu ou a
// changé de version depuis le scan précédent est nommé ; le premier scan pose la référence et le dit.
public sealed class InventaireMachineTests
{
    private static ElementInstalle E(string famille, string nom, string version) => new(famille, nom, version);
    private static readonly DateTimeOffset Hier = new(2026, 10, 2, 21, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Un_pilote_installe_un_service_retire_et_une_mise_a_jour_sont_nommes()
    {
        var avant = new InventaireMachine.Releve(Hier, [
            E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1656"),
            E(InventaireMachine.Services, "AncienService", @"C:\Program Files\X\x.exe"),
            E(InventaireMachine.Services, "Steam Client Service", @"C:\Program Files (x86)\Steam\steamservice.exe"),
            E(InventaireMachine.Logiciels, "Discord", "1.0.9180"),
        ]);
        var apres = new InventaireMachine.Releve(Hier.AddDays(1), [
            E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1714"),
            E(InventaireMachine.Pilotes, "Realtek rt640x64.inf", "10.73.815.2024"),
            E(InventaireMachine.Services, "Steam Client Service", @"C:\Program Files (x86)\Steam\steamservice.exe"),
            E(InventaireMachine.Logiciels, "Discord", "1.0.9180"),
        ]);

        var c = InventaireMachine.Juger(avant, apres).ToList();

        c.Should().HaveCount(2, "les logiciels n'ont pas changé : rien à dire sur eux");
        var pilotes = c.Single(x => x.Nom == "pilotes tiers changés").Explication;
        pilotes.Should().Contain("apparus : Realtek rt640x64.inf 10.73.815.2024")
            .And.Contain("NVIDIA nvmdi.inf 32.0.16.1656 ⇒ 32.0.16.1714").And.Contain("depuis le scan du 02/10");
        c.Single(x => x.Nom == "services tiers changés").Explication.Should().Contain("disparus : AncienService");
    }

    // Une machine fraîche n'a aucun service tiers ; le premier qui apparaît est justement « le truc téléchargé ».
    [Fact]
    public void Une_famille_lue_et_vide_annonce_le_premier_element_qui_apparait()
    {
        var avant = new InventaireMachine.Releve(Hier, [], [InventaireMachine.Services]);
        var apres = new InventaireMachine.Releve(Hier.AddDays(1),
            [E(InventaireMachine.Services, "TrucService", @"C:\Users\x\AppData\truc.exe")], [InventaireMachine.Services]);

        InventaireMachine.Juger(avant, apres).Should().ContainSingle()
            .Which.Explication.Should().Contain("apparus : TrucService");
    }

    [Fact]
    public void Deux_paquets_du_meme_nom_gardent_toutes_leurs_versions()
    {
        // L'ancien pilote NVIDIA reste gardé à côté du nouveau : les deux s'appellent « NVIDIA nvmdi.inf ».
        var avant = new InventaireMachine.Releve(Hier, [E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1656")]);
        var apres = new InventaireMachine.Releve(Hier.AddDays(1), [
            E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1656"),
            E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1714"),
        ]);

        InventaireMachine.Juger(avant, apres).Single().Explication
            .Should().Contain("NVIDIA nvmdi.inf + 32.0.16.1714 (à côté de 32.0.16.1656)");
    }

    // Les vraies lignes de l'aperçu après les pilotes Realtek de Windows Update. « A + B ⇒ A + B + C » se
    // lisait comme un changement ; un service mis à jour recopiait deux chemins entiers.
    [Theory]
    [InlineData("11.0.6000.374 + 11.0.6000.383", "11.0.6000.374 + 11.0.6000.383 + 11.0.6000.398",
        "+ 11.0.6000.398 (à côté de 11.0.6000.374 + 11.0.6000.383)")]
    [InlineData("1.0.887.0 + 1.0.934.0", "1.0.887.0", "− 1.0.934.0 (reste 1.0.887.0)")]
    [InlineData(@"""C:\WINDOWS\System32\DriverStore\FileRepository\realtekservice.inf_amd64_31dd95d009763f70\RtkAudUService64.exe"" -background",
        @"""C:\WINDOWS\System32\DriverStore\FileRepository\realtekservice.inf_amd64_49e3c0efb244cb40\RtkAudUService64.exe"" -background",
        "mis à jour (RtkAudUService64.exe a changé de dossier)")]
    [InlineData(@"C:\Program Files\WindowsApps\Editeur.AppliX_26.930.3930.0_x64__2p2nqsd0c76g0\app\resources\applix-windows-sandbox-service.exe",
        @"C:\Program Files\WindowsApps\Editeur.AppliX_26.930.4958.0_x64__2p2nqsd0c76g0\app\resources\applix-windows-sandbox-service.exe",
        "mis à jour (applix-windows-sandbox-service.exe a changé de dossier)")]
    [InlineData("1.0", "2.0", "1.0 ⇒ 2.0")]
    public void Un_ecart_de_version_se_lit_sans_ambiguite(string avant, string apres, string attendu)
        => InventaireMachine.Ecart(avant, apres).Should().Be(attendu);

    [Fact]
    public void Une_sortie_de_pnputil_illisible_rend_la_famille_non_lue()
    {
        var r = InventaireMachine.Lire(_ => "Commande inconnue");
        r.Lues.Should().NotContain(InventaireMachine.Pilotes).And.Contain(InventaireMachine.Logiciels);
    }

    [Fact]
    public void Une_famille_illisible_cette_fois_n_est_pas_annoncee_disparue()
    {
        // pnputil absent (Windows ancien) ou trop lent sous charge : la famille revient vide, pas « tout désinstallé ».
        var avant = new InventaireMachine.Releve(Hier, [
            E(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1656"),
            E(InventaireMachine.Logiciels, "Discord", "1.0.9180"),
        ]);
        var apres = new InventaireMachine.Releve(Hier.AddDays(1), [E(InventaireMachine.Logiciels, "Discord", "1.0.9181")]);

        var c = InventaireMachine.Juger(avant, apres).ToList();

        c.Should().ContainSingle().Which.Nom.Should().Be("logiciels changés");
    }

    [Fact]
    public void Le_premier_scan_pose_la_reference_et_le_dit()
    {
        var c = InventaireMachine.Juger(null, new InventaireMachine.Releve(Hier, [E(InventaireMachine.Logiciels, "Steam", "2.10")])).Single();
        c.Nom.Should().Be("point de référence");
        c.Explication.Should().Contain("1 logiciels").And.Contain("prochain scan");
    }

    [Fact]
    public void Rien_n_a_change_rien_n_est_dit()
    {
        var r = new InventaireMachine.Releve(Hier, [E(InventaireMachine.Logiciels, "Steam", "2.10")]);
        InventaireMachine.Juger(r, r with { Quand = Hier.AddHours(1) }).Should().BeEmpty();
    }

    [Fact]
    public void Les_pilotes_tiers_se_lisent_dans_la_sortie_reelle_de_pnputil()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xml = File.ReadAllText(Path.Combine(d!.FullName, "tests", "DrDxt.Detection.Tests", "Gpu", "pnputil-pilotes-2026-10-03.xml"));
        InventaireMachine.PilotesTiers(xml).Single().Should().Be(new ElementInstalle(InventaireMachine.Pilotes, "NVIDIA nvmdi.inf", "32.0.16.1656", "08/20/2026"));
    }

    // Le vrai registre et le vrai pnputil de la machine, dans un fichier temporaire : premier passage = référence,
    // second passage juste après = rien n'a changé (ou presque : un service peut démarrer entre les deux).
    [Fact]
    public void Sur_la_vraie_machine_deux_releves_successifs_sont_stables()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"inventaire-test-{Guid.NewGuid():N}.json");
        try
        {
            var premier = InventaireMachine.ComparerEtGarder(fichier, DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil);
            premier.Single().Nom.Should().Be("point de référence");
            premier[0].Explication.Should().Contain("logiciels").And.Contain("services tiers");
            InventaireMachine.ComparerEtGarder(fichier, DrDxt.Detection.Gpu.MagasinDePilotes.Pnputil)
                .Should().BeEmpty("rien n'a été installé entre deux relevés à une seconde d'écart");
        }
        finally { File.Delete(fichier); }
    }

    // À l'ouverture de l'appli, ce qui a changé depuis le dernier scan est relu sans toucher à
    // la référence (le scan suivant le juge encore) ; sans référence, rien n'est dit.
    [Fact]
    public void L_apercu_dit_ce_qui_a_change_sans_deplacer_la_reference()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"inventaire-{Guid.NewGuid():N}.json");
        try
        {
            InventaireMachine.Apercu(fichier, _ => "<PnpUtil/>").Should().BeEmpty();
            File.WriteAllText(fichier, System.Text.Json.JsonSerializer.Serialize(new InventaireMachine.Releve(Hier,
                [E(InventaireMachine.Logiciels, "Logiciel-Disparu-De-Test", "1.0")], [InventaireMachine.Logiciels])));
            var reference = File.ReadAllText(fichier);

            InventaireMachine.Apercu(fichier, _ => "<PnpUtil/>")
                .Should().ContainSingle(c => c.Explication.Contains("disparus : Logiciel-Disparu-De-Test"));
            File.ReadAllText(fichier).Should().Be(reference);
        }
        finally { File.Delete(fichier); }
    }
}
