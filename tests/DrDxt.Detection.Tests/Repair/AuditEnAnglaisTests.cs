using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les constats du scan (usure des disques, démarrage, température de la carte) se disent en anglais sur un Windows anglais.
public sealed class AuditEnAnglaisTests
{
    [Fact]
    public void Les_constats_du_scan_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        RepairAuditService.ValeurDUsure(3, 41).Should().Be("3%, temperature 41 °C");
        RepairAuditService.JugerBcd(null, eleve: false).Single().Explication
            .Should().StartWith("The boot configuration could not be read. DrDXT is running without administrator rights");
        RepairAuditService.TemperatureCarteNonLue([], DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu("x"))
            .Should().Be("NVML returned no temperature, and certainly not zero");
    }

    [Fact]
    public void Le_remede_d_un_service_remplacable_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        var sousProgramData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Editeur", "svc.exe");

        RepairAuditService.Remede(sousProgramData, "x").Should().StartWith("This folder is chosen by the software's publisher");
    }

    [Fact]
    public void La_suspension_usb_et_les_plantages_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        SuspensionUsb.JugerEntrees(3, 0).ValeurCourante.Should().Be("enabled on 0 of 3");
        PlantagesDeJeu.Juger(null, new HashSet<string>(), [], [], DateTimeOffset.UtcNow).Single().Explication
            .Should().StartWith("the Application log could not be read");
    }

    [Fact]
    public void Le_lien_pcie_et_les_echecs_de_mise_a_jour_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        var lien = DrDxt.Detection.Gpu.BranchementDeLaCarte.JugerLien(new("RTX 4060", 16, 8, 4, 4)).Single();
        RepairLibelles.Nom(lien.Nom).Should().Be("PCIe link of RTX 4060", "le nom reste la clé française du journal ; seul l'affichage le traduit");
        lien.Explication.Should().StartWith("your card can work at x16 and runs at x8.");

        DrDxt.Detection.Hardware.MisesAJourEnEchec.Juger([], 90).Single().Explication
            .Should().Be("no update installation failed over 90 days.");
    }

    [Fact]
    public void Le_demarrage_de_windows_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        var f = DrDxt.Detection.Settings.BcdInterpretation.Juger("useplatformclock", "Yes");
        f.ValeurAttendue.Should().Be("element absent");
        f.Explication.Should().StartWith("Windows does not set this itself");
    }

    [Fact]
    public void Le_plan_d_alimentation_et_le_cache_de_shaders_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        ModeAlimentation.JugerPlan("a1841308-3541-4fab-bc81-f71556f20b4a").ValeurCourante.Should().Be("Power saver");
        DemarrageEtDisque.JugerCacheDeShaders(20L << 30).Single().ValeurCourante.Should().Be("20.0 GB");
    }

    [Fact]
    public void Les_mesures_de_demarrage_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        DrDxt.Detection.Hardware.DemarrageMesure.Juger([], DateTimeOffset.UtcNow, TimeSpan.FromDays(30)).Single().Explication
            .Should().StartWith("Windows recorded no startup measurement");
        DrDxt.Detection.Hardware.DemarrageMesure.JugerBios([]).Single().Explication
            .Should().StartWith("this PC's BIOS does not publish");
    }

    [Fact]
    public void Ecran_pilotes_et_peripheriques_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        DrDxt.Detection.Display.EcranBride.Juger(144, 240, 10, "2560×1440")!.ValeurCourante.Should().Be("240 Hz advertised, 144 Hz offered");
        DrDxt.Detection.Hardware.PeripheriquesEnErreur.Juger("Souris", 10)!.ValeurCourante.Should().Be("driver failing (code 10)");
    }

    [Fact]
    public void L_inventaire_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        InventaireMachine.Ecart("531.18", "531.18 + 581.57").Should().Be("+ 581.57 (alongside 531.18)");
    }

    [Fact]
    public void Vidage_pilote_et_hvci_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        DrDxt.Detection.Settings.CrashDumpPolicy.Juger(3).ValeurCourante.Should().Be("3 (small dump)");
        DrDxt.Detection.Gpu.VersionDuPilote.SansSource("AMD", "25.9.1", "amd.com").Explication
            .Should().Be("DrDXT cannot yet read the latest version published by AMD: check on amd.com.");
    }

    [Fact]
    public void Souris_et_fils_de_rendu_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        RepairLibelles.Nom(DrDxt.Detection.Hardware.FrequenceSouris.Juger("G Pro", 125).Nom).Should().Be("Mouse polling rate · G Pro");
        DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Juger((-1, "options.4.cod23.cst"), 8).Single().ValeurCourante
            .Should().Be("automatic (the game decides)");
    }

    [Fact]
    public void Carte_chaude_et_dpc_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        DrDxt.Detection.Latency.LatenceDpc.Remede("nvlddmkm.sys").Should().Be("The remedy is the driver of nvlddmkm.sys (update or earlier version)");
        DrDxt.Detection.Gpu.CarteChaudeEnPartie.Juger([new("Warzone", DateTimeOffset.Now, null, 83)]).Single().ValeurCourante
            .Should().Be("up to 83 °C");
    }

    [Fact]
    public void Batterie_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        DrDxt.Detection.Hardware.UsureBatterie.Juger(50000, 45000).Single().Explication
            .Should().Be("your battery still holds 90% of its original capacity.");
    }
}
