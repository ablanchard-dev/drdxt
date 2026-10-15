using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// L'hygiene entre dans la production — et sept series doivent rester alignees.
///
/// `FrameSeriesHygiene` etait appelee par `tools/smoke` **et par personne d'autre** : la production
/// calculait sur la serie BRUTE, chargement et alt-tab compris. Le plan appelait ce branchement
/// « le plus gros gain de justesse pour le plus petit diff ». La lecture du code dit le contraire,
/// et c'est ce que ce fichier protege.
///
/// `FrameTimesMs` et SIX autres series (`RenderPresentLatencyMs`, `GpuBusyMs`, `GpuWaitMs`,
/// `CpuBusyMs`, `InPresentApiMs`, `AnimationErrorMs`) sont appariees **par index** : le ratio de la
/// frame i se lit a l'index i de chacune. Filtrer la premiere seule decale les six autres, et
/// chaque ratio du classifieur de goulot devient faux **sans lever la moindre erreur**. C'est
/// exactement la classe de defaut que ce depot traque : celui qui ne se voit pas.
///
/// D'ou un MASQUE, applique par run — la ou les sept series sont encore coherentes entre elles.
/// </summary>
public class BenchmarkRunnerHygieneTests
{
    /// <summary>Une capture ou les sept series sont coherentes : la valeur de chaque serie
    /// annexe vaut l'indice de sa frame, ce qui rend tout decalage IMMEDIATEMENT visible.</summary>
    private static FrameCapture Capture(IReadOnlyList<double> frames)
    {
        var indices = Enumerable.Range(0, frames.Count).Select(i => (double)i).ToList();
        return new FrameCapture(frames, indices, indices, indices, indices, indices, indices);
    }

    private static IPresentMonSession Session(IReadOnlyList<double> frames)
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        s.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(Capture(frames)));
        return s;
    }

    /// <summary>Un plafond hors focus : 30 fps NET pendant `secondes`, la signature d'un jeu
    /// endormi derriere une autre fenetre. C'est le seul cas que l'hygiene doit retirer.</summary>
    private static IEnumerable<double> Plafond(int secondes)
    {
        var restant = secondes * 1000.0;
        var i = 0;
        while (restant > 0)
        {
            // +/-0,15 ms : la gigue d'un timer, pas celle d'un jeu qui peine.
            var v = 33.3 + (i++ % 3 - 1) * 0.15;
            yield return v;
            restant -= v;
        }
    }

    private static IEnumerable<double> Jeu(int secondes, int graine)
    {
        var alea = new Random(graine);
        var restant = secondes * 1000.0;
        while (restant > 0)
        {
            var v = 7.0 + (alea.NextDouble() * 2 - 1) * 0.8;
            yield return v;
            restant -= v;
        }
    }

    [Fact]
    public async Task Les_secondes_hors_focus_ne_comptent_plus_dans_le_resultat()
    {
        // 60 s de jeu, 10 s de plafond, 60 s de jeu. Sans hygiene, le plafond tire les FPS moyens
        // vers le bas et le produit publie un defaut que la machine n'a pas.
        var serie = Jeu(60, 1).Concat(Plafond(10)).Concat(Jeu(60, 2)).ToList();
        var brut = FrametimeStatistics.AvgFps(serie);

        var r = await new BenchmarkRunner(Session(serie)).RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().HaveCountLessThan(serie.Count, "les secondes de plafond sortent");
        r.Value.AvgFps.Should().BeGreaterThan(brut, "le resultat cesse d'etre tire par du non-gameplay");
    }

    [Fact]
    public async Task LES_SEPT_SERIES_RESTENT_APPARIEES_APRES_LE_FILTRE()
    {
        // La garde de ce lot. Chaque serie annexe porte l'indice de sa frame ; si le filtre ne
        // s'applique qu'a `FrameTimesMs`, les six autres gardent leurs premieres valeurs et le
        // ratio de la frame i se lit sur une autre frame. Aucune exception, un chiffre faux.
        var serie = Jeu(60, 3).Concat(Plafond(10)).Concat(Jeu(60, 4)).ToList();

        var r = await new BenchmarkRunner(Session(serie)).RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.IsOk.Should().BeTrue();
        var garde = r.Value!.FrameTimesMs.Count;

        // `MedianRatioOrNull` Refuse de calculer quand les longueurs different : un null ici
        // signifie une desynchronisation, pas une absence de mesure.
        r.Value.GpuWaitRatio.Should().NotBeNull("un ratio null trahit des series de longueurs differentes");
        r.Value.CpuBusyRatio.Should().NotBeNull();
        r.Value.MeanGpuBusyMs.Should().NotBeNull();
        r.Value.PresentStallMsP95.Should().NotBeNull();

        garde.Should().BeLessThan(serie.Count, "le filtre doit vraiment avoir retire quelque chose");
    }

    [Fact]
    public async Task Le_resultat_PORTE_l_exposition_reelle_de_la_mesure()
    {
        // Sans ce chiffre, deux passes A/B peuvent avoir vecu des choses differentes sans que rien
        // ne le dise : un alt-tab de vingt secondes d'un seul cote suffit a fabriquer un gain.
        // C'est `AbComparer.Evidence` qui s'en sert pour REFUSER de comparer l'incomparable.
        var serie = Jeu(60, 10).Concat(Plafond(20)).Concat(Jeu(60, 11)).ToList();

        var r = await new BenchmarkRunner(Session(serie)).RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.Value!.HygieneKeptFraction.Should().NotBeNull("un champ que personne ne remplit est un orphelin");
        r.Value.HygieneKeptFraction.Should().BeLessThan(1.0, "le plafond hors focus a bien ete retire");
        r.Value.HygieneKeptFraction.Should().BeApproximately(
            (double)r.Value.FrameTimesMs.Count / serie.Count, 1e-9,
            "l'exposition doit etre le rapport reel, pas une estimation");
    }

    [Fact]
    public async Task Une_mesure_PROPRE_rend_une_exposition_de_1()
    {
        // Le repere mesure : sur les 2 170 secondes de jeu reel du 01/09, l'hygiene retire 0 %.
        // Une passe saine vaut donc exactement 1,0, et deux passes saines sont a zero d'ecart.
        var r = await new BenchmarkRunner(Session(Jeu(120, 12).ToList()))
            .RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.Value!.HygieneKeptFraction.Should().Be(1.0);
    }

    [Fact]
    public async Task Une_serie_SANS_rien_a_retirer_traverse_l_hygiene_intacte()
    {
        // Le cas des 8 captures reelles mesurees le 01/09 : rejouees a travers l'hygiene, elles
        // gardent toutes leurs trames. Le branchement ne doit rien changer pour elles.
        var serie = Jeu(120, 5).ToList();

        var r = await new BenchmarkRunner(Session(serie)).RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.Value!.FrameTimesMs.Should().HaveCount(serie.Count);
    }

    [Fact]
    public async Task Un_ralentissement_REEL_traverse_l_hygiene_et_reste_dans_le_resultat()
    {
        // La regle du projet, verifiee au niveau du produit et pas seulement de la fonction pure :
        // un jeu qui rame est le RESULTAT de la mesure, jamais un artefact a nettoyer.
        var alea = new Random(9);
        var rame = new List<double>();
        var restant = 10_000.0;
        while (restant > 0) { var v = 40.0 + (alea.NextDouble() * 2 - 1) * 12.0; rame.Add(v); restant -= v; }

        var serie = Jeu(60, 6).Concat(rame).Concat(Jeu(60, 7)).ToList();

        var r = await new BenchmarkRunner(Session(serie)).RunAsync(1234, TimeSpan.FromSeconds(1), 1, default);

        r.Value!.FrameTimesMs.Should().HaveCount(serie.Count, "aucune trame de gameplay ne se supprime");
    }
}
