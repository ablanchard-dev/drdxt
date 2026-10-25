using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class MemoryBaselineServiceTests
{
    private sealed class FauxFichier : IMemoryBaselineFile
    {
        public string? Contenu;
        public bool EcritureCassee;
        public bool LectureCassee;

        public Result WriteAtomic(string json)
        {
            if (EcritureCassee) return Result.Fail(ErrorCodes.MemoryBaselineIo, "disque plein");
            Contenu = json;
            return Result.Ok();
        }

        public Result<string?> Read()
            => LectureCassee
                ? Result<string?>.Fail(ErrorCodes.MemoryBaselineIo, "disque illisible")
                : Result<string?>.Ok(Contenu);
    }

    private sealed class FauxBanc : IMemoryBench
    {
        private readonly MemoryMeasurement _rendu;
        public int Lancements;

        public FauxBanc(MemoryMeasurement rendu) => _rendu = rendu;

        public MemoryMeasurement Measure() { Lancements++; return _rendu; }
    }

    private static MemoryMeasurement Mesure(double bandwidthGBs, double sigma = 3.34) => new(
        bandwidthGBs, sigma, 71.33, 4.53,
        new[] { new StaircasePoint(32 * 1024, 0.95), new StaircasePoint(128 * 1024 * 1024, 75.33) });

    private static MemoryModule[] Barrettes(int mts = 3800) =>
    [
        new("PN", "Kingston", mts, 4000),
        new("PN", "Kingston", mts, 4000),
    ];

    private static DateTimeOffset Horloge() => new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);

    private static (MemoryBaselineService service, FauxBanc banc, FauxFichier fichier) Monter(
        MemoryMeasurement rendu)
    {
        var fichier = new FauxFichier();
        var banc = new FauxBanc(rendu);
        var service = new MemoryBaselineService(
            banc, new MemoryBaselineStore(fichier),
            () => new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero));
        return (service, banc, fichier);
    }

    // La jauge du scan prévoit la durée de l'étape d'après MesureraPourScan : il annonce ce que le scan fait ensuite.
    [Theory]
    [InlineData("vide", 3800, false, true)]
    [InlineData("meme vitesse", 3800, false, false)]
    [InlineData("vitesse changee", 3600, false, true)]
    [InlineData("vide", 3800, true, false)]
    [InlineData("lecture en panne", 3800, false, false)]
    public void MesureraPourScan_annonce_ce_que_le_scan_fait(string etat, int mts, bool jeu, bool attendu)
    {
        var (service, banc, fichier) = Monter(Mesure(34.20));
        if (etat is "meme vitesse" or "vitesse changee") service.ProvenChannelsForScan("MACHINE-A", Barrettes());
        if (etat == "lecture en panne") fichier.LectureCassee = true;
        var avant = banc.Lancements;

        service.MesureraPourScan("MACHINE-A", Barrettes(mts), jeu).Should().Be(attendu);
        service.ProvenChannelsForScan("MACHINE-A", Barrettes(mts), jeu);
        (banc.Lancements > avant).Should().Be(attendu, "l'annonce et le scan suivent la même règle");
    }

    // Le joueur ajoute une barrette, ou déplace les siennes, à la même fréquence : la référence décrivait une autre
    // configuration, et relue telle quelle elle gardait « un seul canal probable » sur une machine corrigée.
    [Fact]
    public void Une_reference_prise_avec_d_autres_barrettes_n_est_plus_celle_de_la_machine()
    {
        var (service, banc, _) = Monter(Mesure(17.0));
        MemoryModule[] une = [new("PN", "Kingston", 3200, 3200, DeviceLocator: "DIMMA1")];
        MemoryModule[] deplacees = [new("PN", "Kingston", 3200, 3200, DeviceLocator: "DIMMA2")];
        MemoryModule[] deux = [.. une, new("PN", "Kingston", 3200, 3200, DeviceLocator: "DIMMB1")];
        service.ProvenChannelsForScan("MACHINE-A", une);

        service.MesureCourante("MACHINE-A", une).Should().NotBeNull();
        service.MesureCourante("MACHINE-A", deux).Should().BeNull();
        service.MesureCourante("MACHINE-A", deplacees).Should().BeNull();
        service.MesureraPourScan("MACHINE-A", deux).Should().BeTrue();
        service.MesureraPourScan("MACHINE-A", deplacees).Should().BeTrue();
        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public void Le_premier_scan_mesure_enregistre_et_prouve_les_canaux()
    {
        var (service, banc, fichier) = Monter(Mesure(34.20));

        service.ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(2);

        banc.Lancements.Should().Be(1);
        fichier.Contenu.Should().NotBeNull();
    }

    [Fact]
    public void Les_scans_suivants_ne_relancent_PAS_le_banc()
    {
        // La decision produit : auto au premier scan, a la demande ensuite. Un scan qui remesure
        // a chaque fois couterait 20 a 30 secondes et 384 Mo pour rien.
        var (service, banc, _) = Monter(Mesure(34.20));

        service.ProvenChannelsForScan("MACHINE-A", Barrettes());
        service.ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(2);

        banc.Lancements.Should().Be(1);
    }

    // Une reference prise pendant que Warzone tourne est faussee (12,7 Go/s au lieu de 33,4, mesure).
    // Jeu lance : aucune mesure, rien d'enregistre, rien de prouve.
    [Fact]
    public void Jeu_lance_le_banc_ne_tourne_pas_et_rien_n_est_enregistre()
    {
        var (service, banc, fichier) = Monter(Mesure(34.20));

        service.ProvenChannelsForScan("MACHINE-A", Barrettes(), jeuEnCours: true).Should().Be(0);

        banc.Lancements.Should().Be(0);
        fichier.Contenu.Should().BeNull();
    }

    [Fact]
    public void Jeu_lance_une_reference_deja_prise_reste_lue()
    {
        // Le garde porte sur la mesure, pas sur la lecture : relire ne coute rien au jeu.
        var (service, banc, _) = Monter(Mesure(34.20));
        service.ProvenChannelsForScan("MACHINE-A", Barrettes());

        service.ProvenChannelsForScan("MACHINE-A", Barrettes(), jeuEnCours: true).Should().Be(2);

        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public void Une_mesure_rejetee_n_est_pas_enregistree_et_ne_prouve_rien()
    {
        // Machine occupee : sigma 9 %. La reference resterait fausse pour toujours.
        // Le service reessaie sur Q-03 : mesure, 4 rejets sur 10 sur une machine qui travaille.
        var (service, banc, fichier) = Monter(Mesure(34.20, sigma: 9.0));

        service.ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(0);

        banc.Lancements.Should().Be(MemoryBaselineService.MaxAttempts);
        fichier.Contenu.Should().BeNull();
    }

    [Fact]
    public void L_AB_compare_la_mesure_du_jour_a_la_reference_et_tranche_a_3_sigma()
    {
        // Le retour du BIOS : XMP vient d'etre active. 19,1 -> 34,2 Go/s, soit +79 % -- trois
        // ordres de grandeur au-dessus du bruit de l'instrument.
        var fichier = new FauxFichier();
        var avant = new MemoryBaselineService(
            new FauxBanc(Mesure(19.1)), new MemoryBaselineStore(fichier), Horloge);
        avant.ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 2133));

        var apres = new MemoryBaselineService(
            new FauxBanc(Mesure(34.20)), new MemoryBaselineStore(fichier), Horloge);
        var verdict = apres.CompareAfterChange("MACHINE-A", Barrettes(mts: 3800));

        verdict!.HasVerdict.Should().BeTrue();
        verdict.BandwidthSignificant.Should().BeTrue();
        verdict.BandwidthDeltaPct.Should().BeApproximately(79.06, 0.5);
    }

    [Fact]
    public void Un_A_B_qui_a_tranche_devient_la_nouvelle_reference()
    {
        // Sinon le scan suivant comparerait encore a une configuration que la machine a quittee.
        var fichier = new FauxFichier();
        new MemoryBaselineService(new FauxBanc(Mesure(19.1)), new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 2133));

        var apres = new MemoryBaselineService(
            new FauxBanc(Mesure(34.20)), new MemoryBaselineStore(fichier), Horloge);
        apres.CompareAfterChange("MACHINE-A", Barrettes(mts: 3800));

        new MemoryBaselineStore(fichier).Read("MACHINE-A").Value!.ConfiguredMts.Should().Be(3800);
    }

    [Fact]
    public void Une_frequence_qui_a_BAISSE_reste_comparable()
    {
        // Le piege : le plafond theorique se calcule sur la frequence du jour. Si le client
        // repasse en JEDEC, la mesure d'avant (prise a 3800) depasse le plafond d'aujourd'hui et
        // Q-01 la rejetterait -- le produit se tairait exactement quand il a quelque chose a dire.
        var fichier = new FauxFichier();
        new MemoryBaselineService(new FauxBanc(Mesure(34.20)), new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 3800));

        var verdict = new MemoryBaselineService(
                new FauxBanc(Mesure(19.1)), new MemoryBaselineStore(fichier), Horloge)
            .CompareAfterChange("MACHINE-A", Barrettes(mts: 2133));

        verdict!.HasVerdict.Should().BeTrue();
        verdict.BandwidthDeltaPct.Should().BeNegative();
    }

    /// <summary>Rend les mesures d'une file, dans l'ordre : de quoi jouer une machine qui
    /// s'agite puis se calme.</summary>
    private sealed class BancEnSerie : IMemoryBench
    {
        private readonly Queue<MemoryMeasurement> _file;
        public int Lancements;

        public BancEnSerie(params MemoryMeasurement[] mesures) => _file = new Queue<MemoryMeasurement>(mesures);

        public MemoryMeasurement Measure()
        {
            Lancements++;
            return _file.Count > 1 ? _file.Dequeue() : _file.Peek();
        }
    }

    private sealed class BancQuiLeve : IMemoryBench
    {
        public MemoryMeasurement Measure() => throw new OutOfMemoryException("384 Mo indisponibles");
    }

    [Fact]
    public void Un_banc_qui_leve_n_emporte_pas_le_scan()
    {
        // Le banc alloue 384 Mo. Sur une machine qui n'en a pas -- c'est-a-dire exactement celle
        // dont on veut parler -- l'allocation echoue. Mesurer la memoire ne doit jamais etre la
        // raison pour laquelle le scan d'une petite machine s'arrete.
        var service = new MemoryBaselineService(
            new BancQuiLeve(), new MemoryBaselineStore(new FauxFichier()), Horloge);

        service.ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(0);
        service.CompareAfterChange("MACHINE-A", Barrettes()).Should().BeNull();
    }

    [Fact]
    public void Une_reference_amputee_ne_fait_pas_tomber_le_scan_elle_le_fait_remesurer()
    {
        // Le scan appelle ce service en plein milieu de son parcours : une exception ici et
        // l'ecran affiche « echec du scan » pour un fichier a moitie ecrit.
        var fichier = new FauxFichier { Contenu = "{\"MachineId\":\"MACHINE-A\",\"ConfiguredMts\":3800}" };
        var banc = new FauxBanc(Mesure(34.20));
        var service = new MemoryBaselineService(banc, new MemoryBaselineStore(fichier), Horloge);

        service.ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(2);

        banc.Lancements.Should().Be(1);
    }

    [Fact]
    public void Un_A_B_sur_une_reference_amputee_se_tait_au_lieu_de_lever()
    {
        var fichier = new FauxFichier { Contenu = "{\"MachineId\":\"MACHINE-A\",\"ConfiguredMts\":3800}" };
        var service = new MemoryBaselineService(
            new FauxBanc(Mesure(34.20)), new MemoryBaselineStore(fichier), Horloge);

        service.CompareAfterChange("MACHINE-A", Barrettes()).Should().BeNull();
    }

    [Fact]
    public void Sans_reference_il_n_y_a_rien_a_comparer_et_l_A_B_se_tait()
    {
        var (service, _, _) = Monter(Mesure(34.20));

        service.CompareAfterChange("MACHINE-A", Barrettes()).Should().BeNull();
    }

    [Fact]
    public void Une_frequence_qui_a_change_depuis_la_reference_la_rend_caduque()
    {
        // La reference a ete prise a 2133 MT/s ; la machine tourne maintenant a 3800. Le plafond
        // d'un canal n'est plus le meme, donc le compte de canaux qu'elle portait ne vaut plus.
        var (service, banc, _) = Monter(Mesure(34.20));
        service.ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 2133));

        service.ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 3800));

        banc.Lancements.Should().Be(2);
    }

    [Fact]
    public void Une_machine_qui_s_agite_puis_se_calme_finit_par_donner_sa_reference()
    {
        // Mesure, pas intuition : sur 10 lancements du banc le 07/09 sur la machine de reference,
        // 4 ont ete REJETES en Q-03 alors que les moyennes restaient a 2 % les unes des autres.
        // Un seul essai perdrait donc la reference deux fois sur cinq, pour une machine qui
        // travaille normalement. Q-03 a raison de refuser ; ce qui manquait, c'est de reessayer.
        var banc = new BancEnSerie(
            Mesure(34.20, sigma: 9.0),   // une compilation en fond
            Mesure(34.20, sigma: 7.0),   // ca s'ameliore
            Mesure(34.20, sigma: 3.34)); // machine calmee : celle-la passe
        var fichier = new FauxFichier();

        new MemoryBaselineService(banc, new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(2);

        banc.Lancements.Should().Be(3);
        fichier.Contenu.Should().NotBeNull("la troisieme mesure, acceptee, devient la reference");
    }

    [Fact]
    public void Une_machine_qui_ne_se_calme_JAMAIS_abandonne_au_bout_de_trois_essais()
    {
        // Reessayer sans fin ferait tourner un banc de 384 Mo en boucle sur la machine d'un
        // client qui joue. Trois essais, puis on rend 0 : ne pas savoir, dit franchement.
        var banc = new BancEnSerie(Mesure(34.20, sigma: 9.0));
        var fichier = new FauxFichier();

        new MemoryBaselineService(banc, new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(0);

        banc.Lancements.Should().Be(MemoryBaselineService.MaxAttempts);
        fichier.Contenu.Should().BeNull();
    }

    [Fact]
    public void Un_defaut_d_INSTRUMENT_ne_se_reessaie_pas()
    {
        // Q-04 (latence de cache) ne vient pas de l'agitation de la machine : c'est l'instrument
        // qui mesure la mauvaise chose. Le relancer trois fois rendrait trois fois la meme erreur
        // et couterait une minute au client.
        var duCache = new MemoryMeasurement(34.20, 1.0, 25.0, 1.0,
            new[] { new StaircasePoint(32 * 1024, 0.95), new StaircasePoint(128 * 1024 * 1024, 25.0) });
        var banc = new BancEnSerie(duCache);

        new MemoryBaselineService(banc, new MemoryBaselineStore(new FauxFichier()), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(0);

        banc.Lancements.Should().Be(1);
    }

    // ── Le motif du refus est lui-meme une affirmation ────────────────────────────────────────
    // L'ecran dit au client « une des deux mesures n'a pas ete acceptee (X) » et lui propose
    // d'agir. Repondre Q-03 (« machine occupee, relance au repos ») quand le banc n'a pas pu
    // allouer, ou quand l'instrument mesurait du cache, envoie le client fermer des programmes
    // pour un probleme qui n'a rien a voir. Un motif faux est pire que pas de motif.

    private static MemoryBaselineStore AvecReference(FauxFichier fichier)
    {
        new MemoryBaselineService(new FauxBanc(Mesure(19.1)), new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 2133));
        return new MemoryBaselineStore(fichier);
    }

    [Fact]
    public void Un_banc_indisponible_ne_se_deguise_PAS_en_machine_occupee()
    {
        var fichier = new FauxFichier();
        AvecReference(fichier);

        var verdict = new MemoryBaselineService(
                new BancQuiLeve(), new MemoryBaselineStore(fichier), Horloge)
            .CompareAfterChange("MACHINE-A", Barrettes(mts: 3800));

        verdict!.HasVerdict.Should().BeFalse();
        verdict.NoVerdictReason.Should().Contain("banc indisponible");
        verdict.NoVerdictReason.Should().NotContain("Q-03", "le banc n'a pas pu tourner, la machine n'y est pour rien");
    }

    [Fact]
    public void Une_faute_d_INSTRUMENT_garde_son_propre_numero()
    {
        var fichier = new FauxFichier();
        AvecReference(fichier);

        // Latence a 25 ns : on mesure du cache, pas la DRAM. C'est Q-04, et fermer Discord n'y
        // changera rien.
        var duCache = new MemoryMeasurement(34.20, 1.0, 25.0, 1.0,
            new[] { new StaircasePoint(32 * 1024, 0.95), new StaircasePoint(128 * 1024 * 1024, 25.0) });

        var verdict = new MemoryBaselineService(
                new BancEnSerie(duCache), new MemoryBaselineStore(fichier), Horloge)
            .CompareAfterChange("MACHINE-A", Barrettes(mts: 3800));

        verdict!.NoVerdictReason.Should().Contain("Q-04");
    }

    [Fact]
    public void Une_machine_vraiment_occupee_dit_bien_Q03()
    {
        // Le contre-controle : sans lui, on pourrait faire passer les deux tests ci-dessus en
        // supprimant Q-03 partout.
        var fichier = new FauxFichier();
        AvecReference(fichier);

        var verdict = new MemoryBaselineService(
                new BancEnSerie(Mesure(34.20, sigma: 9.0)), new MemoryBaselineStore(fichier), Horloge)
            .CompareAfterChange("MACHINE-A", Barrettes(mts: 3800));

        verdict!.NoVerdictReason.Should().Contain("Q-03");
    }

    [Fact]
    public void Une_panne_de_LECTURE_ne_doit_pas_ecraser_une_reference_qui_existe()
    {
        // Une reference AMPUTEE se remplace : elle est inutilisable. Une reference qu'on n'a
        // simplement pas pu lire, c'est autre chose -- elle est peut-etre parfaite. La remplacer
        // sur une erreur d'entree-sortie passagere (antivirus, disque occupe) detruirait la seule
        // mesure d'avant que le client possede.
        var fichier = new FauxFichier();
        new MemoryBaselineService(new FauxBanc(Mesure(19.1)), new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 2133));
        var reference = fichier.Contenu;

        fichier.LectureCassee = true;
        var banc = new FauxBanc(Mesure(34.20));
        new MemoryBaselineService(banc, new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes(mts: 3800)).Should().Be(0);

        banc.Lancements.Should().Be(0, "on ne mesure meme pas : il n'y a rien a faire du resultat");
        fichier.Contenu.Should().Be(reference, "la reference d'origine est intacte");
    }

    [Fact]
    public void Une_ecriture_qui_echoue_ne_fait_pas_mentir_le_scan_en_cours()
    {
        // Le disque refuse d'enregistrer : la mesure, elle, est valide, et ce qu'elle prouve
        // reste vrai pour ce scan. Ce qu'on perd, c'est la memoire du resultat -- le scan suivant
        // remesurera. C'est un cout, pas un mensonge, et c'est ecrit.
        var fichier = new FauxFichier { EcritureCassee = true };
        var banc = new FauxBanc(Mesure(34.20));

        new MemoryBaselineService(banc, new MemoryBaselineStore(fichier), Horloge)
            .ProvenChannelsForScan("MACHINE-A", Barrettes()).Should().Be(2);

        fichier.Contenu.Should().BeNull();
    }

    // ── La mesure d'avant doit dater du depart vers le BIOS ──────────────────────
    // L'horloge du service est figee (06/09 10:00) : la reference enregistree porte cette heure.

    [Fact]
    public void La_mesure_d_avant_voyage_est_prise_et_enregistree()
    {
        var (service, banc, fichier) = Monter(Mesure(34.20));

        service.MesurerMaintenant("MACHINE-A", Barrettes()).Should().BeTrue();

        banc.Lancements.Should().Be(1);
        fichier.Contenu.Should().NotBeNull();
    }

    [Fact]
    public void Une_mesure_d_avant_prise_au_depart_permet_le_verdict()
    {
        var (service, _, _) = Monter(Mesure(34.20));
        service.MesurerMaintenant("MACHINE-A", Barrettes()).Should().BeTrue();

        var v = service.CompareAfterChange("MACHINE-A", Barrettes(), Horloge().AddMinutes(1));

        v!.HasVerdict.Should().BeTrue();
    }

    [Theory]
    [InlineData(24 * 60)]  // la reference date de la veille du depart
    [InlineData(16)]       // juste au-dela de la fenetre
    public void Une_mesure_d_avant_qui_ne_date_pas_du_depart_refuse_le_verdict(int minutesAvantLeDepart)
    {
        var (service, _, _) = Monter(Mesure(34.20));
        service.MesurerMaintenant("MACHINE-A", Barrettes()).Should().BeTrue();

        var v = service.CompareAfterChange("MACHINE-A", Barrettes(), Horloge().AddMinutes(minutesAvantLeDepart));

        v!.HasVerdict.Should().BeFalse();
        v.NoVerdictReason.Should().Be(MemoryBaselineService.AvantPerime);
    }

    // Prise APRES le depart : c'est la mesure d'apres d'un A/B deja rendu (ou une remesure a la demande).
    // Rien a comparer, et surtout pas « mesure d'avant perimee ».
    [Fact]
    public void Une_reference_posee_apres_le_depart_ne_donne_aucune_section()
    {
        var (service, banc, _) = Monter(Mesure(34.20));
        service.MesurerMaintenant("MACHINE-A", Barrettes()).Should().BeTrue();
        var avant = banc.Lancements;

        service.CompareAfterChange("MACHINE-A", Barrettes(), Horloge().AddMinutes(-1)).Should().BeNull();
        banc.Lancements.Should().Be(avant, "rien n'est remesure");
    }

    [Fact]
    public void Sans_heure_de_depart_le_verdict_se_rend_comme_avant()
    {
        var (service, _, _) = Monter(Mesure(34.20));
        service.MesurerMaintenant("MACHINE-A", Barrettes()).Should().BeTrue();

        service.CompareAfterChange("MACHINE-A", Barrettes())!.HasVerdict.Should().BeTrue();
    }
}
