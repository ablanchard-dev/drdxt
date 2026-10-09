using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Rien ne persiste, donc aucune derive n'est detectable.
///
/// Un seul fichier survit a une session de DrDXT : `memory-baseline.json`. Ni les mesures, ni les
/// verdicts, ni les resolutions. Consequence directe : le produit ne peut pas repondre a la
/// question que le joueur pose vraiment — **« mon PC est-il plus lent qu'avant ? »**. Il redecouvre
/// la machine a chaque lancement, et une degradation lente est exactement ce qu'aucun scan unique
/// ne peut voir.
///
/// Ce journal ne « garde des donnees » pas pour le principe : il compare la mesure la plus ancienne
/// a la plus recente, sur la meme machine, **avec la resolution des deux** — donc via le meme
/// modele de preuve que l'A/B. Une derive sous le bruit reste `NoResolvedChange`, jamais une alerte.
///
/// Le journal ne compare jamais deux machines : un identifiant different rend le rapprochement
/// vide de sens, et il vaut mieux ne rien dire que comparer deux PC.
/// </summary>
public class PerformanceJournalTests
{
    private static readonly DateTimeOffset J0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Le JEU fait partie de l'identite d'une mesure, au meme titre que la machine :
    /// deux titres n'ont pas la meme echelle de FPS, et les rapprocher annoncerait une
    /// « degradation » qui n'est qu'un changement de jeu.</summary>
    private static PerformanceSample Mesure(int jour, double avgFps, double marge,
        string machine = "PC-A", string? jeu = "warzone") =>
        new(J0.AddDays(jour), machine, avgFps, marge, Runs: 3, KeptFraction: 1.0, GameId: jeu);

    [Fact]
    public void Une_machine_qui_a_RALENTI_au_dela_du_bruit_est_signalee()
    {
        // 200 → 170 fps en un mois, marges 2 + 2 : -15 % pour une resolution de 2 %.
        var derive = PerformanceJournal.Derive(new[] { Mesure(0, 200, 2), Mesure(30, 170, 2) });

        derive.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        derive.RegressionEnJeuProuvee.Should().BeTrue();
        derive.Game.Preuve.DeltaPct.Should().BeApproximately(-15.0, 0.01);
    }

    [Fact]
    public void Une_machine_STABLE_ne_declenche_aucune_alerte()
    {
        var derive = PerformanceJournal.Derive(new[] { Mesure(0, 200, 4), Mesure(30, 201, 4) });

        derive.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
        derive.QuelqueChoseDeProuve.Should().BeFalse();
    }

    [Fact]
    public void Une_seule_mesure_ne_permet_AUCUNE_comparaison()
    {
        // Le premier lancement. Il n'y a rien a comparer, et il faut le dire au lieu de rendre
        // un zero rassurant.
        var derive = PerformanceJournal.Derive(new[] { Mesure(0, 200, 2) });

        derive.Game.Should().BeNull();
        derive.QuelqueChoseDeProuve.Should().BeFalse();
    }

    [Fact]
    public void Un_journal_VIDE_n_affirme_rien()
    {
        PerformanceJournal.Derive(Array.Empty<PerformanceSample>()).Game.Should().BeNull();
    }

    [Fact]
    public void DEUX_MACHINES_differentes_ne_se_comparent_JAMAIS()
    {
        // Le pire faux positif possible : un disque deplace, un dual-boot, un PC remplace, et
        // le produit annonce « ta machine a perdu 40 % ». Mieux vaut se taire.
        var derive = PerformanceJournal.Derive(new[]
        {
            Mesure(0, 200, 2, machine: "PC-A"),
            Mesure(30, 120, 2, machine: "PC-B"),
        });

        derive.Game.Should().BeNull("deux machines ne se comparent pas");
    }

    [Fact]
    public void La_comparaison_prend_la_PLUS_ANCIENNE_et_la_PLUS_RECENTE_de_cette_machine()
    {
        // L'ordre d'ecriture n'est pas garanti (un journal append-only peut etre relu en desordre) :
        // c'est la DATE qui decide, jamais la position dans la liste.
        var derive = PerformanceJournal.Derive(new[]
        {
            Mesure(15, 190, 2),
            Mesure(30, 170, 2),   // la plus recente
            Mesure(0, 200, 2),    // la plus ancienne
        });

        derive.Game!.Preuve.DeltaPct.Should().BeApproximately(-15.0, 0.01, "200 → 170, pas 190 → 170");
    }

    [Fact]
    public void Une_mesure_dont_l_EXPOSITION_differe_trop_ne_sert_pas_de_reference()
    {
        // Meme regle que l'A/B : deux mesures qui n'ont pas vu la meme chose ne se comparent pas.
        // Ici la reference a perdu la moitie de ses trames -- elle ne peut pas servir d'etalon.
        var reference = Mesure(0, 200, 2) with { KeptFraction = 0.50 };
        var aujourdhui = Mesure(30, 170, 2);

        var derive = PerformanceJournal.Derive(new[] { reference, aujourdhui });

        derive.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
    }

    [Fact]
    public void Le_journal_garde_une_TAILLE_bornee_en_gardant_les_extremes()
    {
        // Un journal qui grossit sans fin finit par etre efface a la main. Le tronquer par la fin
        // detruirait la reference la plus ancienne, c'est-a-dire tout l'interet : on garde donc la
        // Premiere mesure et les plus recentes.
        var beaucoup = Enumerable.Range(0, 500).Select(i => Mesure(i, 200 - i * 0.01, 2)).ToList();

        var elague = PerformanceJournal.Elaguer(beaucoup, maximum: 50);

        elague.Should().HaveCount(50);
        elague[0].Should().Be(beaucoup[0], "la reference la plus ancienne est ce qui rend la derive lisible");
        elague[^1].Should().Be(beaucoup[^1], "et la mesure du jour doit y etre");
    }

    [Fact]
    public void DEUX_JEUX_differents_ne_se_comparent_pas()
    {
        // Le produit mesure le jeu qui tourne, quel qu'il soit. Sans cette garde, une partie a
        // 200 fps suivie d'un titre a 60 sortait « -70 % » : une degradation qui n'etait qu'un
        // changement de jeu, et rien dans le journal ne permettait de le voir.
        var derive = PerformanceJournal.Derive(new[]
        {
            Mesure(0, 200, 2, jeu: "warzone"),
            Mesure(30, 60, 2, jeu: "un-autre-jeu"),
        });

        derive.Game.Should().BeNull("mieux vaut se taire que comparer deux echelles sans rapport");
    }

    [Fact]
    public void Un_jeu_NON_IDENTIFIE_ne_se_compare_a_rien()
    {
        var derive = PerformanceJournal.Derive(new[]
        {
            Mesure(0, 200, 2, jeu: "warzone"),
            Mesure(30, 120, 2, jeu: null),
        });

        derive.Game.Should().BeNull("« inconnu » n'est pas « le meme jeu »");
    }

    [Fact]
    public void UNE_mesure_d_un_autre_jeu_n_empeche_pas_de_conclure_sur_le_SIEN()
    {
        // On FILTRE au lieu de refuser : une partie sur un autre titre au milieu de l'historique
        // ne doit pas condamner pour toujours la comparaison du jeu mesure aujourd'hui.
        var derive = PerformanceJournal.Derive(new[]
        {
            Mesure(0, 200, 2, jeu: "warzone"),
            Mesure(10, 60, 2, jeu: "un-autre-jeu"),
            Mesure(30, 170, 2, jeu: "warzone"),
        });

        derive.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        derive.Game.Preuve.DeltaPct.Should().BeApproximately(-15.0, 0.01,
            "la reference est la premiere mesure de WARZONE, pas la plus ancienne du fichier");
    }

    [Fact]
    public void L_elagage_garde_une_REFERENCE_PAR_JEU()
    {
        // Le defaut trouve en revue : la comparaison se fait par (machine, jeu), et l'elagage
        // ne gardait que la plus ancienne mesure du fichier. L'etalon de tous les autres jeux
        // disparaissait, et leur derive se serait ensuite calculee contre un passe deja degrade —
        // sans que rien ne le signale.
        var journal = new List<PerformanceSample>
        {
            Mesure(0, 200, 2, jeu: "warzone"),
            Mesure(1, 60, 2, jeu: "un-autre-jeu"),
        };
        for (var j = 2; j < 40; j++) journal.Add(Mesure(j, 190, 2, jeu: "warzone"));
        journal.Add(Mesure(40, 45, 2, jeu: "un-autre-jeu"));

        var elague = PerformanceJournal.Elaguer(journal, maximum: 10);

        elague.Should().HaveCount(10);
        elague.Should().Contain(journal[0], "l'etalon de warzone");
        elague.Should().Contain(journal[1], "ET l'etalon de l'autre jeu");
        elague[^1].Should().Be(journal[^1], "la mesure du jour reste");

        // Et le juge conclut toujours sur le jeu elague, contre son propre etalon.
        var derive = PerformanceJournal.Derive(elague);
        derive.Game!.Preuve.DeltaPct.Should().BeApproximately(-25.0, 0.01,
            "60 -> 45 fps sur l'autre jeu, compare a SA reference et pas a celle de warzone");
    }

    [Fact]
    public void Un_journal_ORDONNE_survit_a_l_elagage()
    {
        var journal = new List<PerformanceSample>();
        for (var j = 0; j < 30; j++) journal.Add(Mesure(j, 200 - j, 2));

        var elague = PerformanceJournal.Elaguer(journal, maximum: 5);

        elague.Should().BeInAscendingOrder(e => e.TakenUtc);
        elague[0].Should().Be(journal[0]);
    }

    [Fact]
    public void Une_premiere_mesure_TROP_BRUITEE_ne_sert_pas_d_etalon()
    {
        // Journal reel du 17/09/2026 : 164 ± 28 (17 %) le 11/09, pendant des compilations en fond ;
        // 136 ± 5 en jeu le 16/09 ; puis, ici, une vraie baisse a 110 ± 4. Avec la premiere mesure
        // comme etalon, la resolution vaudrait (28 + 4) / 164 = 19,5 % et la baisse de 33 % serait
        // « non resolue ». Contre l'etalon du 16/09 : -19 % pour 6,6 % de resolution.
        var journal = new[] { Mesure(0, 164, 28), Mesure(5, 136, 5), Mesure(30, 110, 4) };

        var etalon = PerformanceJournal.Etalon(journal);
        var derive = PerformanceJournal.Derive(journal);

        etalon.Should().Be(journal[1], "la premiere mesure assez precise, pas la premiere tout court");
        derive.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        derive.Game!.Preuve.DeltaPct.Should().BeApproximately(-19.1, 0.1);
        derive.Game!.Preuve.ResolutionPct.Should().BeApproximately(6.6, 0.1);
    }

    [Fact]
    public void Quand_TOUT_est_bruite_la_plus_ancienne_reste_l_etalon()
    {
        // Le journal ne se tait pas parce que rien n'est precis : il compare et dit sa resolution.
        var journal = new[] { Mesure(0, 164, 28), Mesure(5, 150, 30) };

        PerformanceJournal.Etalon(journal).Should().Be(journal[0]);
        PerformanceJournal.Derive(journal).Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void L_elagage_garde_l_etalon_que_le_juge_prend_pas_la_plus_vieille()
    {
        var journal = new List<PerformanceSample> { Mesure(0, 164, 28), Mesure(1, 136, 5) };
        for (var j = 2; j < 60; j++) journal.Add(Mesure(j, 135, 4));

        var elague = PerformanceJournal.Elaguer(journal, maximum: 10);

        elague.Should().HaveCount(10);
        elague.Should().Contain(journal[1], "l'etalon reel survit a l'elagage");
        elague.Should().NotContain(journal[0], "la mesure bruitee ne sert a rien et cede sa place");
        PerformanceJournal.Etalon(elague).Should().Be(journal[1]);
    }

    [Fact]
    public void DEUX_SCENES_differentes_ne_se_comparent_pas()
    {
        // Un menu rend bien plus d'images qu'une partie : 300 en lobby puis 136 en partie n'est pas
        // une baisse de 55 %, c'est deux scenes. Le journal se tait.
        var journal = new[]
        {
            Mesure(0, 300, 3) with { Scene = SceneRepresentativity.MenuLobby },
            Mesure(5, 136, 3) with { Scene = SceneRepresentativity.Gameplay },
        };

        PerformanceJournal.Derive(journal).Game.Should().BeNull();
        PerformanceJournal.Etalon(journal).Should().BeNull();
    }

    [Fact]
    public void Une_entree_ANCIENNE_sans_scene_vaut_non_declaree_et_se_compare_a_une_autre_non_declaree()
    {
        // Les journaux ecrits avant le champ restent lisibles et comparables entre eux.
        var journal = new[] { Mesure(0, 200, 2), Mesure(30, 170, 2) with { Scene = SceneRepresentativity.Unknown } };

        PerformanceJournal.Derive(journal).Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
    }

    [Fact]
    public void L_elagage_garde_un_etalon_PAR_SCENE()
    {
        var journal = new List<PerformanceSample>
        {
            Mesure(0, 300, 3) with { Scene = SceneRepresentativity.MenuLobby },
            Mesure(1, 136, 3) with { Scene = SceneRepresentativity.Gameplay },
        };
        for (var j = 2; j < 60; j++) journal.Add(Mesure(j, 135, 3) with { Scene = SceneRepresentativity.Gameplay });

        var elague = PerformanceJournal.Elaguer(journal, maximum: 10);

        elague.Should().Contain(journal[0], "l'etalon du menu");
        elague.Should().Contain(journal[1], "et l'etalon de la partie");
    }
}
