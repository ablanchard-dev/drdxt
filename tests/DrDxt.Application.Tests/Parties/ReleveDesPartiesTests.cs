using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Aucune partie jouée ne laissait de trace. Pendant une partie, des
// captures courtes ; à la fin, une ligne : FPS et 1 % low médians, le goulot le plus vu, le nombre de captures.
public sealed class ReleveDesPartiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 21, 0, 0, TimeSpan.Zero);

    private static CaptureEnPartie C(int minute, double fps, double low, BottleneckState goulot)
        => new(T0.AddMinutes(minute), fps, low, goulot);

    // La part composée et la latence PC à leur médiane ; non lues = null, jamais zéro.
    [Fact]
    public void La_part_composee_et_la_latence_pc_se_resument_a_leur_mediane()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { PartComposee = 0.0, LatencePcMs = 18 },
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound) with { PartComposee = 1.0, LatencePcMs = 22 },
            C(7, 120, 80, BottleneckState.CpuBoundOrAppBound) with { PartComposee = 0.2 },
        ])!;
        p.PartComposeeMediane.Should().Be(0.2);
        p.HorsCarteMoMax.Should().BeNull("aucune capture ne l'a lue");
        ReleveDesParties.Resumer("cod.exe", [C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { HorsCarteMo = 120 },
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound) with { HorsCarteMo = 900 }])!.HorsCarteMoMax.Should().Be(900, "à son pic");
        p.LatencePcMedianMs.Should().Be(20);
        ReleveDesParties.Resumer("cod.exe", [C(1, 130, 95, BottleneckState.CpuBoundOrAppBound), C(4, 140, 100, BottleneckState.CpuBoundOrAppBound)])!
            .PartComposeeMediane.Should().BeNull();
    }

    // Le 1 % low d'une partie se calcule sur toutes ses images, ou, si une fenêtre jouée ne les a pas gardées, comme la médiane
    // des 1 % low de chaque fenêtre, plus haute en général. La partie dit lequel : deux estimateurs ne se comparent pas.
    [Fact]
    public void La_partie_dit_si_son_1_pour_cent_low_porte_sur_toutes_ses_images()
    {
        var images = Enumerable.Repeat(7.0, ReleveDesParties.ImagesMinimum).ToList();
        CaptureEnPartie Avec(int minute) => C(minute, 140, 100, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = images };

        ReleveDesParties.Resumer("cod.exe", [Avec(1), Avec(4)])!.Low1SurToutesLesImages.Should().BeTrue();
        ReleveDesParties.Resumer("cod.exe", [Avec(1), C(4, 140, 100, BottleneckState.CpuBoundOrAppBound)])!
            .Low1SurToutesLesImages.Should().BeFalse("une fenêtre sans ses images : la médiane des fenêtres");
    }

    // Le menu et le lobby ne sont pas la partie : leur latence ne se mêle pas à celle des fenêtres jouées, comme les images.
    [Fact]
    public void La_latence_d_affichage_ne_prend_que_les_fenetres_jouees()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 20, PremierPlan = 1 },
            C(4, 130, 95, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 20, PremierPlan = 1 },
            C(7, 60, 50, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 60, PremierPlan = 0 },
            C(9, 60, 50, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 60, PremierPlan = 0 },
        ])!;

        p.LatenceAffichageMedianMs.Should().Be(20, "les deux fenêtres hors premier plan sont le menu, pas la partie");
    }

    // La latence d'affichage à sa médiane ; aucune capture ne l'a = non livrée, jamais zéro.
    [Fact]
    public void La_latence_d_affichage_se_resume_a_sa_mediane_ou_reste_non_livree()
    {
        var avec = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 24 },
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 20 },
            C(7, 120, 80, BottleneckState.MixedGpuLeaning) with { LatenceAffichageMs = 30 },
        ])!;
        avec.LatenceAffichageMedianMs.Should().Be(24);

        ReleveDesParties.Resumer("cod.exe", [C(1, 130, 95, BottleneckState.CpuBoundOrAppBound), C(4, 140, 100, BottleneckState.CpuBoundOrAppBound)])!
            .LatenceAffichageMedianMs.Should().BeNull();
    }

    [Fact]
    public void Une_partie_se_resume_en_medianes_et_en_goulot_le_plus_vu()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound),
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound),
            C(7, 120, 80, BottleneckState.MixedGpuLeaning),
        ])!;

        p.Jeu.Should().Be("cod.exe");
        p.Captures.Should().Be(3);
        p.FpsMedian.Should().Be(130);
        p.Low1Median.Should().Be(95);
        p.Goulot.Should().Be(BottleneckState.CpuBoundOrAppBound);
        p.Debut.Should().Be(T0.AddMinutes(1));
        p.Fin.Should().Be(T0.AddMinutes(7));
    }

    private static IReadOnlyList<double> Images(double fps, int n, double aCoupMs = 0)
    {
        var images = Enumerable.Repeat(1000.0 / fps, n).ToList();
        if (aCoupMs > 0) images[n / 2] = aCoupMs;
        return images;
    }

    [Fact]
    public void Avec_ses_images_la_partie_se_resume_sur_toutes_les_images_et_non_sur_une_mediane_de_fenetres()
    {
        // Deux minutes à 100 et une à 200 : la médiane des fenêtres dirait 100 ; toutes les images (24 000 en 180 s) disent 133.
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(0, 100, 90, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(100, 6000), Entrees = 1 },
            C(1, 100, 90, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(100, 6000), Entrees = 1 },
            C(2, 200, 150, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(200, 12000, aCoupMs: 100), Entrees = 1 },
        ])!;

        p.FpsMedian.Should().BeApproximately(133.3, 0.2);
        // Le pire 1 % de toute la partie : 240 images, l'à-coup de 100 ms de la 3e minute et 239 images à 10 ms ⇒ 10,375 ms.
        p.Low1Median.Should().BeApproximately(96.4, 0.2, "le 1 % low vient des pires images de toute la partie, pas des fenêtres");
        p.MinutesJouees.Should().BeApproximately(3, 0.01);
    }

    [Fact]
    public void Sans_ses_images_une_capture_garde_les_medianes()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(0, 100, 90, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(100, 6000, aCoupMs: 60) },
            C(1, 140, 100, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = [7.0] },
        ])!;

        p.FpsMedian.Should().Be(120);
        p.MinutesJouees.Should().BeApproximately(1, 0.01, "seule la fenêtre qui a ses images compte ses minutes");
    }

    // Un FPS bloqué au plafond, c'est un menu, un chargement ou une autre fenêtre : pas du jeu.
    [Fact]
    public void Un_plafond_de_menu_une_autre_fenetre_et_l_attente_ne_sont_pas_joues()
    {
        var menu = C(0, 100, 99, BottleneckState.UnknownPartial) with { ImagesMs = Images(100, 6000), Entrees = 1, PremierPlan = 1 };
        var ailleurs = C(1, 30, 29, BottleneckState.UnknownPartial) with { ImagesMs = Images(30, 1800), Entrees = 1, PremierPlan = 0.1 };
        var attente = C(2, 155, 150, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(155, 9300), Entrees = 0.05, PremierPlan = 1 };
        var jeu1 = C(3, 155, 120, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(155, 9300, aCoupMs: 40), Entrees = 0.9, PremierPlan = 1 };
        var jeu2 = C(4, 155, 120, BottleneckState.CpuBoundOrAppBound) with { ImagesMs = Images(155, 9300, aCoupMs: 40), Entrees = 0.8, PremierPlan = 1 };

        var p = ReleveDesParties.Resumer("cod.exe", [menu, ailleurs, attente, jeu1, jeu2])!;

        p.Captures.Should().Be(5);
        p.MinutesJouees.Should().BeApproximately(2, 0.01);
        p.FpsMedian.Should().BeApproximately(155, 0.5, "le menu à 100 et l'autre fenêtre à 30 ne pèsent pas");
        p.Goulot.Should().Be(BottleneckState.CpuBoundOrAppBound);
        ReleveDesParties.EstJouee(attente with { Entrees = null }, 155).Should().BeTrue("un plafond AU niveau de la partie (cap en jeu) reste joué");
        ReleveDesParties.EstJouee(menu, 155).Should().BeFalse();
        ReleveDesParties.EstJouee(menu with { ImagesMs = Images(100, 6000, aCoupMs: 60) }, 155).Should().BeTrue("des images qui bougent ne sont pas un plafond");
    }

    [Fact]
    public void Une_partie_passee_au_lobby_reste_relevee_avec_zero_minute_jouee()
    {
        var lobby = C(0, 100, 99, BottleneckState.UnknownPartial) with { ImagesMs = Images(100, 6000), Entrees = 0.1 };
        var p = ReleveDesParties.Resumer("cod.exe", [lobby, lobby with { Quand = T0.AddMinutes(1) }])!;
        p.MinutesJouees.Should().Be(0);
        p.FpsMedian.Should().BeApproximately(100, 0.1);
    }

    // En partie sous 90 % de ce que le banc de 45 s tient, le processeur ralentit en jeu.
    [Theory]
    [InlineData(4300.0, 5190.0, true)]
    [InlineData(5100.0, 5190.0, false)]
    [InlineData(null, 5190.0, false)]
    [InlineData(4300.0, null, false)]
    public void Le_processeur_ralentit_en_partie_sous_neuf_dixiemes_du_banc(double? enPartie, double? banc, bool attendu)
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 5, 120, 90,
            BottleneckState.CpuBoundOrAppBound, enPartie, 120, BancMhz: banc);
        ReleveDesParties.RalentitEnPartie(p).Should().Be(attendu);
    }

    // Pas « la chaleur » dès 90 °C : avec les secondes relevées, la raison est la plus vue.
    [Fact]
    public void La_cause_du_ralentissement_vient_des_secondes_freinees_quand_elles_sont_lues()
    {
        var p = new PartieRelevee("cod.exe", T0, T0.AddMinutes(16), 16, 155, 25, BottleneckState.CpuBoundOrAppBound, 3944, 57,
            CelsiusMax: 93, BancMhz: 5100);

        ReleveDesParties.CauseDuRalentissement(p).Should().Be(("Parties.RalentitChaleur", (double?)null), "sans secondes : l'ancienne règle");
        ReleveDesParties.CauseDuRalentissement(p with { Freinage = new FreinageCpu(966, 41, 0, 0, 544, 88, 100, 73) })
            .Should().Be(("Parties.RalentitCourant", (double?)56));
        ReleveDesParties.CauseDuRalentissement(p with { Freinage = new FreinageCpu(600, 10, 0, 5, 0, 85, 98, 70) })
            .Should().Be(("Parties.Ralentit", (double?)null), "aucune raison au-dessus de 5 % : la cause n'est pas nommée");
    }

    // La carte prenait la raison la plus vue (5 %, sans PROCHOT), le docteur exigeait un quart du temps
    // pour le courant et la puissance : courant 10 % + chaleur 8 % faisait dire « courant » à la carte et « chaleur » au docteur.
    // Une seule règle (FreinageCpu.Raison), et la carte dit ce que dit le docteur, pour toute combinaison.
    [Fact]
    public void La_carte_dit_toujours_la_meme_raison_que_le_docteur()
    {
        var p = new PartieRelevee("cod.exe", T0, T0.AddMinutes(10), 10, 155, 25, BottleneckState.CpuBoundOrAppBound, 3944, 57,
            CelsiusMax: 93, BancMhz: 5100);
        ReleveDesParties.CauseDuRalentissement(p with { Freinage = new FreinageCpu(600, 48, 0, 0, 60, 85, 98, 70) })
            .Should().Be(("Parties.RalentitChaleurLue", (double?)8), "courant sous le quart : c'est la chaleur, comme le docteur");
        ReleveDesParties.CauseDuRalentissement(p with { Freinage = new FreinageCpu(600, 0, 180, 0, 0, 80, 92, 70) })
            .Should().Be(("Parties.RalentitProchot", (double?)30));

        var attendue = new Dictionary<RaisonDuFreinage, string>
        {
            [RaisonDuFreinage.Courant] = "Parties.RalentitCourant", [RaisonDuFreinage.Chaleur] = "Parties.RalentitChaleurLue",
            [RaisonDuFreinage.Prochot] = "Parties.RalentitProchot", [RaisonDuFreinage.Puissance] = "Parties.RalentitPuissance",
            [RaisonDuFreinage.PasFreine] = "Parties.Ralentit",
        };
        int[] parts = [0, 20, 40, 100, 160, 300];
        foreach (var chaleur in parts) foreach (var prochot in parts) foreach (var w in parts) foreach (var courant in parts)
        {
            var f = new FreinageCpu(600, chaleur, prochot, w, courant, 85, 98, 70);
            ReleveDesParties.CauseDuRalentissement(p with { Freinage = f }).Cle.Should().Be(attendue[f.Raison()], $"{f}");
        }
    }

    [Fact]
    public void Les_captures_sans_releve_processeur_ne_faussent_pas_la_mediane()
    {
        var t = DateTimeOffset.Now;
        var p = ReleveDesParties.Resumer("cod.exe", [
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound, 5000, 130),
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound),
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound, 4000, 110)])!;
        p.MhzMedian.Should().Be(4500, "la capture sans relevé n'est pas un zéro");
        // Deux captures : une seule ne fait plus une partie.
        ReleveDesParties.Resumer("cod.exe", [new CaptureEnPartie(t, 120, 90, BottleneckState.GpuBound),
                new CaptureEnPartie(t, 120, 90, BottleneckState.GpuBound)])!
            .MhzMedian.Should().BeNull();
    }

    [Fact]
    public void Sans_capture_il_n_y_a_pas_de_partie_a_retenir()
        => ReleveDesParties.Resumer("cod.exe", []).Should().BeNull();

    [Fact]
    public void Un_goulot_inconnu_ne_l_emporte_pas_sur_un_goulot_mesure()
        => ReleveDesParties.Resumer("cod.exe",
            [C(1, 130, 95, BottleneckState.UnknownPartial), C(2, 130, 95, BottleneckState.UnknownPartial), C(3, 130, 95, BottleneckState.GpuBound)])!
            .Goulot.Should().Be(BottleneckState.GpuBound, "« inconnu » n'est pas un verdict qui gagne au nombre");

    [Fact]
    public void Seules_les_vingt_dernieres_parties_sont_gardees()
    {
        var liste = new List<PartieRelevee>();
        for (var i = 0; i < 25; i++)
            liste = ReleveDesParties.Ajouter(liste, new PartieRelevee("cod.exe", T0.AddHours(i), T0.AddHours(i).AddMinutes(20), 3, 100 + i, 80, BottleneckState.GpuBound)).ToList();

        liste.Should().HaveCount(ReleveDesParties.PartiesGardees);
        liste[0].FpsMedian.Should().Be(124, "la plus récente d'abord");
        liste[^1].FpsMedian.Should().Be(105);
    }

    // La partie garde chaque ventilateur de la carte, sur TOUTES ses captures (un ventilateur qui
    // s'arrête dans un menu s'arrête quand même).
    [Fact]
    public void La_partie_garde_ses_ventilateurs_sur_toutes_ses_captures()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { Ventilateurs = [new VentilateurEnPartie(5, 1100, 1000, 0, 60)] },
            C(2, 130, 95, BottleneckState.CpuBoundOrAppBound) with { Ventilateurs = [new VentilateurEnPartie(5, 500, 0, 30, 60)] },
        ])!;
        p.Ventilateurs!.Single().SecondesArret.Should().Be(30);
    }
}
