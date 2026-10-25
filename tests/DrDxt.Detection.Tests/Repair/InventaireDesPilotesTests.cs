using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Une seule règle pour « ancien », et une signature non lue n'est jamais « non signé ».
public sealed class InventaireDesPilotesTests
{
    private static readonly DateTime Maintenant = new(2026, 10, 16);

    private static InventaireDesPilotes.Pilote P(string fournisseur, DateTime? date, bool? signe = true, string inf = "oem20.inf")
        => new("MEDIA", "x", fournisseur, date, signe, inf);

    [Fact]
    public void La_date_du_pilote_est_celle_de_l_INF_quel_que_soit_le_fuseau()
    {
        // DriverDate est une date sans heure écrite à minuit UTC : convertie en heure locale, elle recule d'un jour à
        // l'ouest de Greenwich, et la date jeton 18/07/1968 du chipset Intel (relevée sur la machine de référence) ne se reconnaîtrait plus.
        InventaireDesPilotes.DateDmtf("19680718000000.******+000").Should().Be(new DateTime(1968, 7, 18));
        InventaireDesPilotes.DateDmtf("20060621000000.******-300").Should().Be(new DateTime(2006, 6, 21));
        InventaireDesPilotes.DateDmtf("x").Should().BeNull();
        InventaireDesPilotes.DateDmtf(null).Should().BeNull();
    }

    [Fact]
    public void Les_dates_jeton_et_les_pilotes_de_Windows_ne_sont_pas_anciens()
    {
        InventaireDesPilotes.Ancien(P("INTEL", new(1968, 7, 18), inf: "oem106.inf"), Maintenant).Should().BeFalse("date jeton du chipset Intel");
        InventaireDesPilotes.Ancien(P("Microsoft", new(2006, 6, 21), inf: "display.inf"), Maintenant).Should().BeFalse("pilote de la boîte");
        InventaireDesPilotes.Ancien(P("NVIDIA", new(2010, 1, 1), inf: "nvlt.inf"), Maintenant).Should().BeFalse("pas un paquet tiers publié");
        InventaireDesPilotes.Ancien(P("VB-Audio Software", new(2014, 2, 12)), Maintenant).Should().BeTrue();
        InventaireDesPilotes.Ancien(P("VB-Audio Software", Maintenant.AddYears(-1)), Maintenant).Should().BeFalse();
    }

    // Le pilote chipset AMD : ses composants (PSP, SMBus, GPIO) installés par AMD sur un processeur AMD. Absent ou ancien,
    // c'est « à regarder » : rien ne prouve un mal, mais Windows seul ne pose pas tout ce qu'AMD livre.
    private static InventaireDesPilotes.Pilote Amd(string nom, DateTime date) =>
        new("SYSTEM", nom, "Advanced Micro Devices, Inc", date, true, "oem42.inf");

    [Fact]
    public void Sur_un_Ryzen_sans_composant_AMD_du_chipset_le_pilote_est_dit_absent()
    {
        var c = InventaireDesPilotes.JugerChipsetAmd([P("Microsoft", Maintenant)], "AuthenticAMD", Maintenant).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.NonDefault);
        c.Explication.Should().Be(Docteur.T("IV.ChipsetAmdAbsent"));
    }

    [Fact]
    public void Un_pilote_chipset_AMD_ancien_est_dit_avec_sa_date_et_un_recent_est_seulement_lu()
    {
        var vieux = InventaireDesPilotes.JugerChipsetAmd([Amd("AMD PSP 10.0 Device", new(2019, 3, 1)), Amd("AMD SMBus", new(2020, 1, 1))],
            "AuthenticAMD", Maintenant).Single();
        vieux.Statut.Should().Be(RepairStatus.NonDefault);
        vieux.ValeurCourante.Should().Contain("2020", "le plus récent des composants fait la date");

        InventaireDesPilotes.JugerChipsetAmd([Amd("AMD GPIO Controller", Maintenant.AddMonths(-6))], "AuthenticAMD", Maintenant)
            .Single().Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Sur_un_processeur_Intel_ou_sans_inventaire_le_chipset_AMD_ne_dit_rien()
    {
        InventaireDesPilotes.JugerChipsetAmd([P("INTEL", Maintenant)], "GenuineIntel", Maintenant).Should().BeEmpty();
        InventaireDesPilotes.JugerChipsetAmd(null, "AuthenticAMD", Maintenant).Should().BeEmpty("l'inventaire dit déjà non lu");
        InventaireDesPilotes.JugerChipsetAmd([], null, Maintenant).Should().BeEmpty();
    }

    [Fact]
    public void Une_signature_non_lue_n_est_pas_un_pilote_non_signe()
    {
        var constats = InventaireDesPilotes.Juger([P("A", Maintenant, signe: null), P("B", Maintenant, signe: false)], Maintenant).ToList();

        constats.Single(c => c.Nom == "pilotes non signes").ValeurCourante.Should().Be("x");
        constats.Single(c => c.Nom == "inventaire des pilotes").ValeurCourante.Should().Be("2 pilotes, 1 non signé(s), 0 ancien(s), 1 signature non lue");
        InventaireDesPilotes.Juger(null, Maintenant).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
}
