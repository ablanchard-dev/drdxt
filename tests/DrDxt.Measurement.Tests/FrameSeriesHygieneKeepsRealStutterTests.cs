using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Le controle positif qui manquait : un vrai ralentissement doit survivre a l'hygiene.
///
/// `Clean` ecarte toute seconde dont la mediane depasse 25 ms. L'intention est de retirer les
/// secondes passees derriere une autre fenetre — un jeu hors focus se cale sur son plafond, 30 fps
/// soit 33,3 ms. Mais le critere ne distingue pas ce plafond d'un jeu qui **rame vraiment** : a
/// 25 fps soutenus, chaque seconde a une mediane de 40 ms et disparait. Le produit publierait alors
/// une machine saine la ou le joueur a vu son jeu se trainer — le defaut exactement inverse de
/// celui que l'hygiene corrige.
///
/// Pourquoi ces series sont synthetiques, et pourquoi c'est le bon choix.
/// Mesure sur les 8 captures reelles du 01/09 : la coupe hors-focus y supprime **une seule seconde au total**,
/// sur 2 171 secondes, et cette seconde ne contient **qu'une trame de 795 ms**. Le danger n'est donc
/// pas observable dans ces donnees : la machine de reference tourne a ~140 fps et n'a jamais rame.
/// L'absence de danger observe n'etant pas l'absence de danger, il faut le fabriquer pour le tester.
///
/// (La plus longue capture conservee dans le depot fait 595 s.)
/// </summary>
public class FrameSeriesHygieneKeepsRealStutterTests
{
    /// <summary>N secondes de trames a `ms` (+/- `gigue`), deterministe.</summary>
    private static IEnumerable<double> Segment(double ms, int secondes, double gigue, int graine)
    {
        var alea = new Random(graine);
        var restant = secondes * 1000.0;
        while (restant > 0)
        {
            var v = ms + (alea.NextDouble() * 2 - 1) * gigue;
            if (v <= 0) v = ms;
            yield return v;
            restant -= v;
        }
    }

    // 7 ms = ~143 fps, le regime reel de la machine de reference.
    private static List<double> Jeu(int secondes, int graine) => Segment(7.0, secondes, 0.8, graine).ToList();

    [Fact]
    public void Un_ralentissement_SOUTENU_appartient_au_resultat()
    {
        // 25 fps pendant 10 s, encadres de jeu normal : ce que le joueur appelle « ca rame ».
        // Irregulier, comme un vrai ralentissement — pas un plafond.
        var serie = Jeu(60, 1).Concat(Segment(40.0, 10, 12.0, 2)).Concat(Jeu(60, 3)).ToList();

        var propre = FrameSeriesHygiene.Clean(serie);

        propre.Applied.Should().BeTrue();
        propre.OutOfFocusSecondsDropped.Should().Be(0,
            "un jeu qui rame est le RESULTAT de la mesure, pas un artefact a nettoyer");
        // Et le chiffre que le client lit doit en porter la trace.
        FrametimeStatistics.P1LowFps(propre.FrameTimesMs)
            .Should().BeLessThan(FrametimeStatistics.AvgFps(propre.FrameTimesMs) * 0.75,
                "le 1 % low doit rester marque par les 10 s de ralentissement");
    }

    [Fact]
    public void Un_plafond_hors_focus_reste_ecarte()
    {
        // Le cas que l'hygiene existe pour traiter : 30 fps NET, la signature d'un jeu endormi
        // derriere une autre fenetre. La periode y est quasi constante — c'est ce qui le distingue.
        var serie = Jeu(60, 4).Concat(Segment(33.3, 10, 0.4, 5)).Concat(Jeu(60, 6)).ToList();

        var propre = FrameSeriesHygiene.Clean(serie);

        propre.Applied.Should().BeTrue();
        propre.OutOfFocusSecondsDropped.Should().BeGreaterThan(0,
            "un plafond hors focus n'est pas une performance de la machine");
    }

    [Fact]
    public void Une_machine_qui_rame_EN_ENTIER_garde_TOUTES_ses_trames()
    {
        // Le pire diagnostic possible : un PC a 25 fps du debut a la fin. Chaque seconde depasse
        // le seuil de lenteur. Avant le critere de regularite, seule la garde de dernier recours
        // (« garder moins de 20 % ⇒ refuser ») empechait de rendre un resultat vide — un filet,
        // pas une decision. Le critere de regularite rend ce filet inutile ici : plus rien n'est
        // supprime, et le filtre n'a plus besoin de refuser pour bien faire.
        var serie = Segment(40.0, 120, 12.0, 7).ToList();

        var propre = FrameSeriesHygiene.Clean(serie);

        propre.FrameTimesMs.Should().HaveCount(serie.Count, "aucune trame de gameplay ne se supprime");
        propre.OutOfFocusSecondsDropped.Should().Be(0);
    }
}
