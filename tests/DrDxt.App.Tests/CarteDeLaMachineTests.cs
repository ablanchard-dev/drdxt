using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// La carte de la machine : ce que le scan a lu, constate, mesure et recommande, rangé par domaine.
///
/// Elle ne juge rien de neuf. Chaque ligne vient d'un constat, d'une mesure ou d'une recommandation
/// que le produit calculait déjà ; la carte ne fait que les ranger. Les tests tiennent trois
/// promesses : aucun domaine ne disparaît (un domaine vide se dit), un domaine sans lecture n'est
/// jamais « rien de signalé », et rien n'est rangé au hasard.
/// </summary>
public class CarteDeLaMachineTests
{
    private static string Loc(string cle) => cle;

    private static readonly Dictionary<string, IReadOnlyList<string>> Aucune = new();

    private static EntreeCarte Ligne(string domaine, Severity gravite, string titre = "t", bool nonLu = false) =>
        new(domaine, gravite, titre, string.Empty, string.Empty, string.Empty, nonLu);

    [Fact]
    public void Un_reglage_propose_ne_se_range_pas_sous_ce_qui_a_ete_constate()
    {
        // 23/09/2026, scan réel, carte Latence : sous « Constaté », juste après « VRR inactif »,
        // la ligne « VRR + modèle flip activés : moins de latence… » se lisait comme un état de la
        // machine. C'était le réglage que DrDXT propose, rangé avec ce qu'il a lu.
        var vrrInactif = Ligne("latence", Severity.Minor, "VRR inactif");
        var propose = new EntreeCarte("latence", Severity.Info, "VRR + modèle flip activés",
            string.Empty, string.Empty, string.Empty, NonLu: false, Propose: true);

        var latence = CarteDeLaMachine.Construire([vrrInactif, propose], Aucune, [], Loc)
            .Single(d => d.Cle == "latence");

        latence.Lignes.Select(l => l.Titre).Should().Equal("VRR inactif");
        latence.Proposes.Select(l => l.Titre).Should().Equal("VRR + modèle flip activés");
        latence.Etat.Should().Be(EtatDomaine.ACorriger, "l'état se lit toujours sur tout le domaine");
    }

    [Fact]
    public void Un_domaine_qui_n_a_qu_un_reglage_propose_est_a_regarder()
    {
        var propose = new EntreeCarte("latence", Severity.Info, "r", string.Empty, string.Empty,
            string.Empty, NonLu: false, Propose: true);

        CarteDeLaMachine.Construire([propose], Aucune, [], Loc)
            .Single(d => d.Cle == "latence").Etat.Should().Be(EtatDomaine.ARegarder);
    }

    // 29/09/2026, critique de l'app : « 12 points à corriger » n'avait aucune liste, il fallait ouvrir les huit
    // domaines un par un avant d'atteindre Optimiser. Sous le verdict : ce qui est à corriger, le plus grave
    // d'abord, cinq au plus ; ni information, ni angle mort, ni réglage proposé.
    [Fact]
    public void A_faire_d_abord_liste_les_constats_du_plus_grave_au_moins_grave()
    {
        var carte = CarteDeLaMachine.Construire(
        [
            Ligne("stockage", Severity.Minor, "disque"),
            Ligne("memoire", Severity.Major, "xmp"),
            Ligne("processeur", Severity.Minor, "plan"),
            Ligne("reseau", Severity.Info, "info"),
            Ligne("graphique", Severity.Major, "pilote", nonLu: true),
            new EntreeCarte("latence", Severity.Minor, "propose", string.Empty, string.Empty, string.Empty, NonLu: false, Propose: true),
        ], Aucune, [], Loc);

        var aFaire = CarteDeLaMachine.AFaire(carte);

        aFaire.Select(p => p.Entree.Titre).Should().Equal("xmp", "plan", "disque");
        aFaire[0].Domaine.Cle.Should().Be("memoire", "un clic ouvre le domaine du point");
    }

    // 29/09/2026, critique de l'app : un constat mémoire faisait dix lignes d'un bloc. La première phrase reste
    // à l'écran, le reste s'ouvre sous « Pourquoi ? ».
    [Fact]
    public void Un_detail_long_montre_sa_premiere_phrase_et_garde_la_suite_a_deplier()
    {
        var e = new EntreeCarte("memoire", Severity.Minor, "t", "Première phrase. Deuxième phrase. Troisième.", string.Empty, string.Empty, false);

        e.DetailCourt.Should().Be("Première phrase.");
        e.DetailSuite.Should().Be("Deuxième phrase. Troisième.");
        e.ADetailSuite.Should().BeTrue();

        var court = new EntreeCarte("memoire", Severity.Minor, "t", "Une seule phrase.", string.Empty, string.Empty, false);
        court.DetailCourt.Should().Be("Une seule phrase.");
        court.ADetailSuite.Should().BeFalse("rien à déplier, pas de « Pourquoi ? »");
    }

    [Fact]
    public void Une_mesure_longue_montre_sa_premiere_phrase_et_garde_la_suite_a_deplier()
    {
        var l = new LigneMesure("Mesuré : 4,72 ms de gigue. La mesure a eu lieu. Ce sont des pings.", null);

        l.TexteCourt.Should().Be("Mesuré : 4,72 ms de gigue (variation du délai).");
        l.Suite.Should().Be("La mesure a eu lieu. Ce sont des pings.");
        new LigneMesure("Fichier d'échange : 21504 Mo", null).ASuite.Should().BeFalse();
    }

    // 29/09/2026, critique (joueur qui ne connaît rien au BIOS) : dix-neuf lignes brutes, et aucune phrase
    // pour dire qu'il n'y a rien à toucher. Une longue liste mesurée montre six lignes, le reste se déplie.
    [Fact]
    public void Une_longue_liste_mesuree_montre_six_lignes_et_replie_le_reste()
    {
        var mesures = Enumerable.Range(1, 19).Select(i => $"Réglage {i}").ToList();
        var bios = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>> { ["bios"] = mesures }, [], Loc)
            .Single(d => d.Cle == "bios");

        bios.MesuresVisibles.Select(m => m.Texte).Should().Equal(mesures.Take(6));
        bios.MesuresRepliees.Should().HaveCount(13);

        var court = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>> { ["bios"] = mesures.Take(8).ToList() }, [], Loc)
            .Single(d => d.Cle == "bios");
        court.MesuresRepliees.Should().BeEmpty("huit lignes se lisent d'un coup, replier deux lignes ne sert à rien");
    }

    // 29/09/2026 (boucle « vivant et prouvé », B9) : un domaine s'ouvrait sur des listes. Il s'ouvre sur une phrase qui
    // dit quoi faire : le plus grave d'abord, sinon le réglage à essayer, sinon rien à faire.
    [Fact]
    public void Chaque_domaine_s_ouvre_sur_une_phrase_qui_dit_quoi_faire()
    {
        var entrees = new[]
        {
            new EntreeCarte("graphique", Severity.Major, "Mode NVIDIA au maximum", "d", "", "", NonLu: false),
            new EntreeCarte("graphique", Severity.Info, "Température", "d", "", "", NonLu: false),
            new EntreeCarte("latence", Severity.Info, "VRR", "d", "", "", NonLu: false, Propose: true),
        };
        var mesures = new Dictionary<string, IReadOnlyList<string>> { ["stockage"] = ["Usure : 0 %"] };
        var d = CarteDeLaMachine.Construire(entrees, mesures, [], k => k + "|{0}").ToDictionary(x => x.Cle);

        d["graphique"].Verdict.Should().Be("Carte.Verdict.ACorriger|mode NVIDIA au maximum.", "le plus grave d'abord, sans majuscule après le deux-points, point final");
        d["latence"].Verdict.Should().Be("Carte.Verdict.AEssayer|VRR.");
        d["stockage"].Verdict.Should().Be("Carte.Verdict.Rien|{0}");
    }

    // 29/09/2026, photo (latence) : « Mode DLSS du jeu : Balanced », et l'explication (ce que le mode Performance
    // retire, gain non mesuré) seulement en info-bulle, que personne ne découvre. Elle passe sous « Pourquoi ? ».
    [Fact]
    public void L_explication_d_une_ligne_lue_s_ouvre_sous_pourquoi()
    {
        var ligne = new LigneMesure("Mode DLSS du jeu : Balanced", "En Performance, 26 % de pixels en moins.");

        ligne.TexteCourt.Should().Be("Mode DLSS du jeu : Balanced");
        ligne.ASuite.Should().BeTrue();
        ligne.Suite.Should().Contain("26 % de pixels en moins");
        new LigneMesure("Fréquence : 200 Hz", null).ASuite.Should().BeFalse("témoin : rien à déplier");
    }

    // 29/09/2026, photo (latence) : « pas encore validé sur une vraie machine » dans la pastille en police de code
    // des valeurs lues, comme un chiffre mesuré. Une réserve se lit comme une phrase ; une valeur lue garde sa pastille.
    [Fact]
    public void La_reserve_d_un_reglage_propose_n_est_pas_une_valeur_lue()
    {
        var propose = new EntreeCarte("latence", Severity.Info, "T", "D", "pas encore validé", "", NonLu: false, Propose: true);
        var lu = new EntreeCarte("stockage", Severity.Info, "T", "D", "0 %", "", NonLu: false);

        propose.ValeurLue.Should().BeEmpty();
        propose.ReserveDeValidation.Should().Be("pas encore validé");
        lu.ValeurLue.Should().Be("0 %", "témoin : une valeur lue garde sa pastille");
        lu.ReserveDeValidation.Should().BeEmpty();
    }

    // 29/09/2026, critique B10 (passage 6, P1) : « gigue », « premier saut », un code d'arrêt brut, lus sans aucune
    // reformulation avant « Pourquoi ? ». Une glose courte, une fois par ligne, comme les réglages BIOS.
    [Theory]
    [InlineData("Mesuré : 0 ms de gigue au premier saut, 3 ms de gigue au-delà.",
        "Mesuré : 0 ms de gigue (variation du délai) au premier saut (ta box), 3 ms de gigue au-delà.")]
    [InlineData("Incident noyau conservé : 1x 0x144 BUGCODE_USB3_DRIVER, paramètre 1 : 0x1009",
        "Incident noyau conservé : 1x 0x144 BUGCODE_USB3_DRIVER (plantage du pilote USB 3), paramètre 1 : 0x1009")]
    [InlineData("Modération d'interruption : activée", "Modération d'interruption (regroupement des signaux de la carte réseau) : activée")]
    [InlineData("Plan d'alimentation actif : Hautes performances", "Plan d'alimentation actif : Hautes performances")]
    public void Le_jargon_est_glose_une_fois_par_ligne(string texte, string attendu)
    {
        new LigneMesure(texte, null).TexteCourt.Should().Be(attendu);
    }

    // 29/09/2026, critique B10 (passage 5, P1, vu sur photo) : le BIOS était le seul domaine sans « Pourquoi ? ». Un
    // réglage connu porte ce qu'il change ; un inconnu reste sans explication plutôt qu'avec une explication devinée.
    [Fact]
    public void Un_reglage_bios_connu_a_son_pourquoi_un_inconnu_non()
    {
        var lignes = new[] { "XMP (profil de vitesse de la RAM) : Profile 1 (modifié)", "Réglage Maison : Auto (réglage d'usine)" };
        var bios = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>> { ["bios"] = lignes }, [], Loc)
            .Single(d => d.Cle == "bios");

        bios.MesuresAffichees[0].ASuite.Should().BeTrue();
        bios.MesuresAffichees[0].Suite.Should().Contain("JEDEC");
        bios.MesuresAffichees[1].ASuite.Should().BeFalse("témoin : pas d'explication devinée");
        CarteDeLaMachine.ExplicationBios("Above 4G Decoding (prérequis de Re-Size BAR) : Enabled").Should()
            .StartWith("Permet de placer", "la glose cite Re-Size BAR, l'explication reste celle d'Above 4G");
    }

    [Fact]
    public void Chaque_glose_bios_a_son_explication()
    {
        CarteDeLaMachine.FragmentsGloses.Should().OnlyContain(f => CarteDeLaMachine.ExplicationBios($"{f} : Auto") != null,
            "une glose sans explication redonnerait une ligne BIOS sans « Pourquoi ? »");
    }

    // 29/09/2026, critique B10 (passage 4, P0) : la Carte graphique « à corriger » sans aucun geste. Le remède était
    // écrit, mais derrière « Pourquoi ? ». Il sort de l'explication et se lit sans clic.
    [Theory]
    [InlineData("Le mode d'alimentation NVIDIA est au maximum. La carte ne redescend jamais. Pour le retirer du bureau : panneau NVIDIA, « Normal ».",
        "Pour le retirer du bureau : panneau NVIDIA, « Normal ».", "La carte ne redescend jamais.")]
    [InlineData("Ce service tourne en SYSTEM. Un programme pourrait remplacer ce fichier. Réinstaller le logiciel dans Program Files ferme ce chemin. DrDXT ne touche pas à ce service.",
        "Réinstaller le logiciel dans Program Files ferme ce chemin. DrDXT ne touche pas à ce service.", "Un programme pourrait remplacer ce fichier.")]
    [InlineData("Ce service tourne en SYSTEM. Le fichier est déjà sous Program Files : c'est ce droit d'écriture qui ouvre le chemin.",
        "Le fichier est déjà sous Program Files : c'est ce droit d'écriture qui ouvre le chemin.", "")]
    [InlineData("Une tâche réécrivable. Elle se lancerait en administrateur. Déplacer le programme dans Program Files ferme ce chemin.",
        "Déplacer le programme dans Program Files ferme ce chemin.", "Elle se lancerait en administrateur.")]
    public void Le_remede_d_un_constat_se_lit_sans_clic(string detail, string remede, string pourquoi)
    {
        var e = new EntreeCarte("gpu", Severity.Minor, "Titre", detail, "", "", false);
        e.Remede.Should().Be(remede);
        e.DetailSuite.Should().Be(pourquoi, "le remède ne se répète pas derrière « Pourquoi ? »");
    }

    // 29/09/2026, critique B10 (passage 5, P1) : deux commandes icacls à retaper à la main. Le bouton copie les deux,
    // une par ligne : collées dans une console administrateur (cmd ou PowerShell), elles s'exécutent l'une après l'autre.
    [Fact]
    public void La_commande_du_remede_se_copie_telle_quelle()
    {
        var detail = "Ce service tourne en SYSTEM. Un programme pourrait le remplacer. Le fichier est déjà sous Program Files : "
            + "c'est ce droit d'écriture qui ouvre le chemin. Pour le retirer, dans une console administrateur : "
            + "icacls \"C:\\Program Files\\Epic\\U.exe\" /setowner *S-1-5-32-544 puis icacls \"C:\\Program Files\\Epic\\U.exe\" /reset "
            + "(le fichier revient aux Administrateurs).";
        var e = new EntreeCarte("windows", Severity.Minor, "Titre", detail, "", "", false);

        e.CommandeACopier.Should().Be("icacls \"C:\\Program Files\\Epic\\U.exe\" /setowner *S-1-5-32-544\r\n"
                                      + "icacls \"C:\\Program Files\\Epic\\U.exe\" /reset");
        new EntreeCarte("gpu", Severity.Minor, "Titre", "Panneau NVIDIA. Pour le retirer : « Normal ».", "", "", false)
            .CommandeACopier.Should().BeEmpty("témoin : un remède sans commande n'a pas de bouton");
    }

    // 03/10/2026 (D10.5, revue n° 4) : la prose du remède annonce les commandes, elle ne les recopie pas.
    [Fact]
    public void La_prose_du_remede_annonce_les_commandes_sans_les_recopier()
    {
        var detail = "Ce service tourne en SYSTEM. Un programme pourrait le remplacer. Le fichier est déjà sous Program Files : "
            + "c'est ce droit d'écriture qui ouvre le chemin. Pour le retirer, dans une console administrateur : "
            + "icacls \"C:\\Program Files\\Epic\\U.exe\" /setowner *S-1-5-32-544 puis icacls \"C:\\Program Files\\Epic\\U.exe\" /reset "
            + "(le fichier revient aux Administrateurs).";
        var e = new EntreeCarte("windows", Severity.Minor, "Titre", detail, "", "", false);

        e.RemedeLisible.Should().NotContain("icacls").And.Contain("les deux commandes ci-dessous");
        e.CommandeACopier.Should().Contain("/setowner").And.Contain("/reset", "les commandes restent entières pour le bloc et la copie");
        new EntreeCarte("gpu", Severity.Minor, "Titre", "Panneau NVIDIA. Pour le retirer : « Normal ».", "", "", false)
            .RemedeLisible.Should().Be("Pour le retirer : « Normal ».", "sans commande, la prose ne change pas");
    }

    [Fact]
    public void Un_constat_sans_remede_ecrit_n_en_invente_pas()
    {
        var e = new EntreeCarte("windows", Severity.Minor, "Titre", "Arrêts brutaux. Dix redémarrages non propres.", "", "", false);
        e.Remede.Should().BeEmpty();
        e.DetailSuite.Should().Be("Dix redémarrages non propres.", "témoin : l'explication reste entière");
    }

    // 29/09/2026, critique B10 (passage 2) : la page Windows empilait douze constats d'un bloc ; jusqu'à quatre, tout se
    // lit d'un coup. UI1 (critique du 08/10, défaut 3) : « Voir la suite (16, dont 12 à corriger) » cachait douze rouges
    // derrière trois lignes. Tout ce qui est à corriger reste à l'écran ; seul ce qui est pour information se replie.
    [Fact]
    public void Une_longue_liste_garde_a_l_ecran_tout_ce_qui_est_a_corriger_et_replie_l_information()
    {
        var lignes = Enumerable.Range(1, 16)
            .Select(i => new EntreeCarte("windows", i <= 3 ? Severity.Major : i <= 12 ? Severity.Minor : Severity.Info, $"Constat {i}", "", "", "", false))
            .ToList();
        var windows = new DomaineMachine("windows", "Windows", "", EtatDomaine.ACorriger, "", "", [], lignes, [], []);

        windows.LignesVisibles.Should().HaveCount(12).And.OnlyContain(l => l.Gravite > Severity.Info);
        windows.LignesRepliees.Should().HaveCount(4).And.OnlyContain(l => l.Gravite == Severity.Info);

        var peuDeRouges = windows with { Lignes = lignes.Take(1).Concat(lignes.Skip(12)).ToList() };
        peuDeRouges.LignesVisibles.Should().HaveCount(3, "sous trois rouges, les plus graves complètent jusqu'à trois");
        peuDeRouges.LignesRepliees.Should().HaveCount(2);

        var court = windows with { Lignes = lignes.Take(4).ToList() };
        court.LignesVisibles.Should().HaveCount(4, "quatre constats se lisent d'un coup");
        court.ADesLignesRepliees.Should().BeFalse();
    }

    // 29/09/2026, photo (processeur) : « 14 états lus », trois affichés, « Voir les autres lignes : 13 ». Le compte
    // prenait les sous-titres de famille pour des états. Il ne compte que ce qui se lit comme une donnée.
    [Fact]
    public void Le_compte_des_lignes_repliees_ne_compte_pas_les_sous_titres()
    {
        var mesures = Enumerable.Range(1, 19).Select(i => $"Réglage {i}").ToList();
        var bios = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>> { ["bios"] = mesures }, [], Loc)
            .Single(d => d.Cle == "bios") with { Titres = new HashSet<string> { "Réglage 10", "Réglage 15" } };

        bios.MesuresRepliees.Should().HaveCount(13, "témoin : les sous-titres restent dans la liste dépliée");
        bios.NombreDeLignesRepliees.Should().Be(11);
    }

    [Fact]
    public void A_faire_d_abord_s_arrete_a_cinq()
    {
        var carte = CarteDeLaMachine.Construire(
            Enumerable.Range(0, 8).Select(i => Ligne("windows", Severity.Minor, $"p{i}")).ToList(), Aucune, [], Loc);

        CarteDeLaMachine.AFaire(carte).Should().HaveCount(5);
    }

    [Fact]
    public void Les_huit_domaines_sont_toujours_la_dans_le_meme_ordre()
    {
        var carte = CarteDeLaMachine.Construire([], Aucune, [], Loc);

        carte.Select(d => d.Cle).Should().Equal(
            "processeur", "graphique", "memoire", "latence", "reseau", "stockage", "windows", "bios");
    }

    [Fact]
    public void Un_domaine_avec_un_constat_mineur_ou_majeur_est_a_corriger()
    {
        var carte = CarteDeLaMachine.Construire([Ligne("memoire", Severity.Minor)], Aucune, [], Loc);

        carte.Single(d => d.Cle == "memoire").Etat.Should().Be(EtatDomaine.ACorriger);
    }

    [Fact]
    public void Un_domaine_qui_n_a_que_de_l_information_est_a_regarder_pas_a_corriger()
    {
        var carte = CarteDeLaMachine.Construire([Ligne("reseau", Severity.Info)], Aucune, [], Loc);

        carte.Single(d => d.Cle == "reseau").Etat.Should().Be(EtatDomaine.ARegarder);
    }

    [Fact]
    public void Un_domaine_dont_on_n_a_rien_pu_lire_est_non_lu_et_jamais_rien_de_signale()
    {
        var carte = CarteDeLaMachine.Construire([Ligne("windows", Severity.Info, "température", nonLu: true)], Aucune, [], Loc);

        var windows = carte.Single(d => d.Cle == "windows");
        windows.Etat.Should().Be(EtatDomaine.NonLu, "« pas lu » n'est pas « sain »");
        windows.NonLus.Should().ContainSingle();
        windows.Lignes.Should().BeEmpty();
    }

    [Fact]
    public void Un_angle_mort_ne_masque_pas_un_constat_du_meme_domaine()
    {
        var carte = CarteDeLaMachine.Construire(
            [Ligne("windows", Severity.Major, "arrêts"), Ligne("windows", Severity.Info, "température", nonLu: true)],
            Aucune, [], Loc);

        var windows = carte.Single(d => d.Cle == "windows");
        windows.Etat.Should().Be(EtatDomaine.ACorriger);
        windows.NonLus.Should().ContainSingle();
    }

    [Fact]
    public void Un_domaine_mesure_sans_constat_dit_rien_de_signale()
    {
        var mesures = new Dictionary<string, IReadOnlyList<string>> { ["latence"] = ["FPS moyen : 136 ± 5"] };

        var latence = CarteDeLaMachine.Construire([], mesures, [], Loc).Single(d => d.Cle == "latence");

        latence.Etat.Should().Be(EtatDomaine.RienDeSignale);
        latence.Mesures.Should().Equal("FPS moyen : 136 ± 5");
    }

    [Fact]
    public void Une_mesure_qui_demontre_un_ecart_ne_dit_pas_rien_de_signale()
    {
        // Vu sur la photo du 25/09 : « Rien de signalé » au-dessus de « ton processeur tient 30 % de moins ».
        var mesures = new Dictionary<string, IReadOnlyList<string>> { ["processeur"] = ["tient 30,0 % de moins"] };

        CarteDeLaMachine.Construire([], mesures, [], Loc, mesureDemontree: new HashSet<string> { "processeur" })
            .Single(d => d.Cle == "processeur").Etat.Should().Be(EtatDomaine.ARegarder);
        CarteDeLaMachine.Construire([], mesures, [], Loc)
            .Single(d => d.Cle == "processeur").Etat.Should().Be(EtatDomaine.RienDeSignale);
    }

    [Fact]
    public void Un_domaine_ni_lu_ni_mesure_ni_constate_est_non_lu()
    {
        CarteDeLaMachine.Construire([], Aucune, [], Loc).Should().OnlyContain(d => d.Etat == EtatDomaine.NonLu);
    }

    [Fact]
    public void Les_mesures_vides_ne_font_pas_une_ligne()
    {
        var mesures = new Dictionary<string, IReadOnlyList<string>> { ["processeur"] = ["", "  ", "Soutenu : 3 698 MHz"] };

        CarteDeLaMachine.Construire([], mesures, [], Loc).Single(d => d.Cle == "processeur")
            .Mesures.Should().Equal("Soutenu : 3 698 MHz");
    }

    [Fact]
    public void Les_lignes_sont_triees_de_la_plus_grave_a_la_moins_grave()
    {
        var carte = CarteDeLaMachine.Construire(
            [Ligne("graphique", Severity.Info, "a"), Ligne("graphique", Severity.Major, "b"), Ligne("graphique", Severity.Minor, "c")],
            Aucune, [], Loc);

        carte.Single(d => d.Cle == "graphique").Lignes.Select(l => l.Titre).Should().Equal("b", "c", "a");
    }

    [Fact]
    public void Le_composant_lu_accompagne_son_domaine()
    {
        var composants = new[] { new ComponentItem("Gpu", "Carte graphique", "NVIDIA GeForce RTX 4060") };

        CarteDeLaMachine.Construire([], Aucune, composants, Loc).Single(d => d.Cle == "graphique")
            .Composant.Should().Be("NVIDIA GeForce RTX 4060");
    }

    [Theory]
    [InlineData("Scheduling", "processeur")]
    [InlineData("Gpu", "graphique")]
    [InlineData("Display", "graphique")]
    [InlineData("Memory", "memoire")]
    [InlineData("Latency", "latence")]
    [InlineData("Network", "reseau")]
    [InlineData("Inconnue", "windows")]
    public void Chaque_categorie_de_constat_a_son_domaine(string categorie, string domaine) =>
        CarteDeLaMachine.DomaineDuConstat(categorie).Should().Be(domaine);

    [Theory]
    [InlineData("CPU", "processeur")]
    [InlineData("POWER", "processeur")]
    [InlineData("MMCSS", "processeur")]
    [InlineData("GPU DRIVER", "graphique")]
    [InlineData("GPU", "graphique")]
    [InlineData("DISPLAY", "graphique")]
    [InlineData("RAM", "memoire")]
    [InlineData("PAGEFILE", "memoire")]
    [InlineData("LATENCE", "latence")]
    [InlineData("RESEAU", "reseau")]
    [InlineData("STOCKAGE", "stockage")]
    [InlineData("STABILITE", "windows")]
    [InlineData("BCD", "windows")]
    [InlineData("THERMIQUE", "windows")]
    [InlineData("BIOS", "bios")]
    public void Chaque_domaine_de_l_audit_a_son_domaine(string cle, string domaine) =>
        CarteDeLaMachine.DomaineDeLAudit(cle).Should().Be(domaine);

    [Theory]
    [InlineData("temperature du GPU", "graphique")]
    [InlineData("temperature du processeur", "processeur")]
    [InlineData("autre", "windows")]
    public void La_temperature_se_range_avec_son_composant(string nom, string domaine) =>
        CarteDeLaMachine.DomaineDeLAudit("THERMIQUE", nom).Should().Be(domaine);

    [Theory]
    [InlineData(ProfileIntent.Latency, "latence")]
    [InlineData(ProfileIntent.GpuTune, "graphique")]
    [InlineData(ProfileIntent.FixMisconfig, "processeur")]
    public void Chaque_recommandation_a_son_domaine(ProfileIntent intention, string domaine) =>
        CarteDeLaMachine.DomaineDeLaRecommandation(intention).Should().Be(domaine);

    [Fact]
    public void Le_domaine_a_ouvrir_en_premier_est_le_plus_grave()
    {
        var carte = CarteDeLaMachine.Construire(
            [Ligne("reseau", Severity.Info), Ligne("memoire", Severity.Major)], Aucune, [], Loc);

        CarteDeLaMachine.PremierAOuvrir(carte)!.Cle.Should().Be("memoire");
    }

    [Fact]
    public void A_etat_egal_le_domaine_le_plus_grave_s_ouvre_en_premier()
    {
        // 23/09/2026, scan réel avec XMP coupé : « Latence » (VRR, Mineur) s'ouvrait avant
        // « BIOS » (XMP, Majeur, le plus gros gain réel) parce que les deux étaient « À corriger ».
        var carte = CarteDeLaMachine.Construire(
            [Ligne("latence", Severity.Minor), Ligne("bios", Severity.Major)], Aucune, [], Loc);

        CarteDeLaMachine.PremierAOuvrir(carte)!.Cle.Should().Be("bios");
    }

    [Fact]
    public void Un_domaine_dont_l_audit_a_verifie_des_etats_n_est_jamais_pas_lu()
    {
        // Vu le 17/09/2026 sur la machine de reference : « Stockage : Pas lu » alors que l'audit avait lu
        // l'usure et la température du SSD. La carte ne recevait que les états à regarder.
        var verifies = new Dictionary<string, IReadOnlyList<string>> { ["stockage"] = ["Cache d'écriture", "Trim"] };

        var stockage = CarteDeLaMachine.Construire([], Aucune, [], Loc, verifies).Single(d => d.Cle == "stockage");

        stockage.Etat.Should().Be(EtatDomaine.RienDeSignale);
        stockage.Mesures.Should().Equal(new[] { "Carte.Verifies.Plusieurs :", "Cache d'écriture", "Trim" },
            "on dit LESQUELS, un par ligne, pas seulement combien");
    }

    /// <summary>27/09/2026, vu sur photo (carte graphique) : « 3 états lus par l'audit, sans geste
    /// proposé : » portait une puce, au même rang que les trois états qu'il annonce. Un en-tête n'est
    /// ni une donnée ni un titre de famille : il a son propre rang, les états gardent leur puce.</summary>
    [Fact]
    public void L_en_tete_des_etats_lus_n_est_ni_une_donnee_ni_un_titre_de_famille()
    {
        var verifies = new Dictionary<string, IReadOnlyList<string>> { ["stockage"] = ["Cache d'écriture", "Trim"] };

        var affichees = CarteDeLaMachine.Construire([], Aucune, [], Loc, verifies)
            .Single(d => d.Cle == "stockage").MesuresAffichees;

        affichees.Select(l => l.EstEntete).Should().Equal(true, false, false);
        affichees.Should().OnlyContain(l => !l.EstTitre, "aucun titre de famille ici");
    }

    /// <summary>24/09/2026 : l'explication sourcée d'un état lu n'était visible nulle part à l'écran.
    /// Elle voyage avec sa ligne, pour l'info-bulle ; une ligne sans explication n'en porte pas.</summary>
    [Fact]
    public void L_explication_d_un_etat_lu_voyage_avec_sa_ligne()
    {
        var verifies = new Dictionary<string, IReadOnlyList<string>> { ["stockage"] = ["Usure : 0 %", "Trim"] };
        var explications = new Dictionary<string, string> { ["Usure : 0 %"] = "Endurance consommée lue par SMART." };

        var lignes = CarteDeLaMachine.Construire([], Aucune, [], Loc, verifies, explications)
            .Single(d => d.Cle == "stockage").MesuresAffichees;

        lignes.Should().ContainEquivalentOf(new LigneMesure("Usure : 0 %", "Endurance consommée lue par SMART."));
        lignes.Single(l => l.Texte == "Trim").Explication.Should().BeNull();
    }

    [Fact]
    public void Un_seul_etat_verifie_se_dit_au_singulier()
    {
        var verifies = new Dictionary<string, IReadOnlyList<string>> { ["reseau"] = ["Pertes de l'interface"] };

        CarteDeLaMachine.Construire([], Aucune, [], Loc, verifies).Single(d => d.Cle == "reseau")
            .Mesures.First().Should().Contain("Carte.Verifies.Un");
    }

    [Fact]
    public void Des_etats_verifies_ne_masquent_pas_un_constat_du_meme_domaine()
    {
        var verifies = new Dictionary<string, IReadOnlyList<string>> { ["windows"] = ["Vidage mémoire"] };

        CarteDeLaMachine.Construire([Ligne("windows", Severity.Minor)], Aucune, [], Loc, verifies)
            .Single(d => d.Cle == "windows").Etat.Should().Be(EtatDomaine.ACorriger);
    }

    [Fact]
    public void Le_BIOS_lu_s_affiche_dans_son_domaine_avec_le_libelle_du_menu()
    {
        var lus = new[]
        {
            new BiosSettingReading("Extreme Memory Profile(X.M.P)", 1, "Profile 1", IsDefault: false),
            new BiosSettingReading("Intel Turbo Boost", 1, "Enabled", IsDefault: true),
        };
        var mesures = new Dictionary<string, IReadOnlyList<string>> { ["bios"] = CarteDeLaMachine.LignesBios(lus) };

        var bios = CarteDeLaMachine.Construire([], mesures, [], Loc).Single(d => d.Cle == "bios");

        // 25/09/2026 : vu sur photo, les réglages d'usine étaient marqués et les réglages changés
        // ne l'étaient pas ; l'écran se taisait sur les deux lignes qui comptent. Même mot que le rapport.
        bios.Mesures.Should().Equal(
            "Extreme Memory Profile(X.M.P) (profil de vitesse de la RAM) : Profile 1 (modifié)",
            "Intel Turbo Boost (montée en fréquence automatique du processeur) : Enabled (réglage d'usine)");
        CarteDeLaMachine.LignesBios(null).Should().BeEmpty("rien de lu : aucune ligne inventée");
    }

    // 29/09/2026 (boucle « vivant et prouvé », B12, critique B10 P1) : les sigles du BIOS n'étaient pas expliqués. Le
    // nom exact reste (on le retrouve dans le menu du BIOS), une glose courte le suit ; un réglage inconnu reste nu.
    [Theory]
    [InlineData("XMP", "XMP (profil de vitesse de la RAM) : Profile 1 (modifié)")]
    [InlineData("Re-Size BAR", "Re-Size BAR (la carte graphique voit toute sa mémoire d'un coup) : Profile 1 (modifié)")]
    [InlineData("CSM", "CSM (démarrage compatible anciens systèmes) : Profile 1 (modifié)")]
    [InlineData("Réglage inconnu", "Réglage inconnu : Profile 1 (modifié)")]
    public void Un_sigle_du_BIOS_garde_son_nom_et_porte_une_glose(string reglage, string attendu)
        => CarteDeLaMachine.LignesBios([new BiosSettingReading(reglage, 1, "Profile 1", IsDefault: false)]).Single().Should().Be(attendu);

    [Theory]
    [InlineData("Tes écrans tournent à des fréquences différentes. Sur certaines cartes NVIDIA, ça coûte.",
        "Tes écrans tournent à des fréquences différentes.", "Sur certaines cartes NVIDIA, ça coûte.")]
    [InlineData("XMP inactif", "XMP inactif", "")]
    [InlineData("Plafond à 0.5 ms fixé.", "Plafond à 0.5 ms fixé.", "")]
    public void Un_constat_long_se_coupe_en_titre_et_explication(string texte, string titre, string suite)
        => CarteDeLaMachine.TitreEtSuite(texte).Should().Be((titre, suite));

    [Fact]
    public void Une_frequence_memoire_lue_qui_ne_concorde_pas_avec_Windows_le_dit()
    {
        var lu = new[] { new BiosSettingReading("DRAM Frequency", 84, "3800 G1 (38x1.00x100.00)", IsDefault: false) };

        CarteDeLaMachine.LignesBios(lu, 3800).Single().Should().Be("DRAM Frequency (vitesse de la RAM) : 3800 G1 (38x1.00x100.00) (modifié)");
        CarteDeLaMachine.LignesBios(lu, 3200).Single().Should().Contain("lecture non concordante");
        CarteDeLaMachine.LignesBios(lu).Single().Should().NotContain("concordante", "sans mesure Windows, rien à comparer");
    }

    [Fact]
    public void Le_lien_PCIe_se_dit_sans_crier_au_bridage_pour_une_economie_d_energie()
    {
        // Mesuré le 24/09/2026 sur la RTX 4060 : Gen4 x8, son maximum (la carte est câblée x8).
        CarteDeLaMachine.LignePcie(new(4, 8, 4, 8)).Should()
            .Be("Lien PCIe de la carte graphique : Gen4 x8 (maximum de la carte : Gen4 x8)");
        CarteDeLaMachine.LignePcie(new(4, 4, 4, 16)).Should().Contain("moins de lignes");
        CarteDeLaMachine.LignePcie(new(1, 8, 4, 8)).Should().Contain("économie d'énergie").And.NotContain("moins de lignes");
    }

    [Fact]
    public void Les_sous_titres_de_famille_ne_comptent_pas_comme_des_etats_lus()
    {
        // 25/09/2026, photo de la page Windows : 20 lignes à plat. Rangées par famille, les
        // sous-titres s'intercalent ; le compte « N états lus » doit rester celui des états.
        var verifies = new Dictionary<string, IReadOnlyList<string>>
        {
            ["windows"] = ["Stabilité", "Type de vidage : 3", "Services", "Steam : 2x", "GameInput : 1x"],
        };
        var titres = new HashSet<string> { "Stabilité", "Services" };

        var windows = CarteDeLaMachine.Construire([], Aucune, [], k => k == "Carte.Verifies.Plusieurs" ? "{0} états lus" : k, verifies, titres: titres)
            .Single(d => d.Cle == "windows");

        windows.Mesures[0].Should().Be("3 états lus :", "trois états, deux sous-titres");
        windows.MesuresAffichees.Where(l => l.EstTitre).Select(l => l.Texte)
            .Should().Equal("Stabilité", "Services");
        windows.MesuresAffichees.Count(l => !l.EstTitre && !l.EstEntete).Should().Be(3, "les trois états");
    }

    // 03/10/2026 (D12.1, revue n° 6) : un terme déjà suivi de sa parenthèse reçoit la glose dedans, pas une seconde
    // parenthèse collée ; le nom du processeur perd ses marques déposées.
    [Fact]
    public void La_glose_entre_dans_la_parenthese_existante()
    {
        CarteDeLaMachine.GloserLeJargon("0 ms au premier saut (192.168.1.254) contre 0,4 ms")
            .Should().Be("0 ms au premier saut (ta box, 192.168.1.254) contre 0,4 ms");
        CarteDeLaMachine.GloserLeJargon("instable dès le premier saut, puis")
            .Should().Be("instable dès le premier saut (ta box), puis", "sans parenthèse, la glose garde la sienne");
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-14700F", "Intel Core i7-14700F")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "AMD Ryzen 7 7800X3D 8-Core Processor")]
    public void Le_nom_du_processeur_perd_ses_marques_deposees(string brut, string lisible) =>
        ScanViewModel.NomLisible(brut).Should().Be(lisible);

    // Vu sur capture (08/10/2026, après le lot UI) : la pastille « Instant Replay allumé : environ 5 % d'images en moins »
    // disparaissait parce que « 5 » figurait dans la partie de l'explication repliée derrière le « ? ». Ne redit rien que ce
    // qui est AFFICHÉ (titre et première phrase).
    [Fact]
    public void Une_valeur_dite_seulement_dans_la_partie_repliee_reste_affichee()
    {
        var e = new EntreeCarte("graphique", Severity.Info, "Enregistrement continu NVIDIA",
            "L'Instant Replay enregistre en continu les dernières minutes de jeu. Coût publié : environ 5 % d'images selon NVIDIA.",
            "Instant Replay allumé : environ 5 % d'images en moins selon NVIDIA", "", false);
        e.ValeurAAfficher.Should().Be("Instant Replay allumé : environ 5 % d'images en moins selon NVIDIA");

        var pilote = new EntreeCarte("graphique", Severity.Minor, "Version du pilote graphique",
            "Ton pilote NVIDIA 617.14 a une version de retard : la dernière publiée est la 617.42. Mets-le à jour.",
            "NVIDIA 617.14 installé → 617.42 disponible", "", false);
        pilote.ValeurAAfficher.Should().BeEmpty("la première phrase dit déjà les deux versions");
    }
}
