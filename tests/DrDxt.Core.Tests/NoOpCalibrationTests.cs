using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le no-op est la seule verite terrain que nous aurons jamais.
///
/// Un candidat qui n'ecrit rien a un effet vrai de zero, par construction. Ce que la chaine y
/// trouve mesure donc son erreur, directement, sans hypothese. Ces tests verifient que le juge de
/// calibration sait dire les trois choses qui comptent — et qu'il refuse de trancher trop tot.
///
/// Les deux erreurs sont graves et ne se ressemblent pas :
///   * SURCONFIANT — le produit garde un reglage inerte ;
///   * sous-CONFIANT : le produit ne voit jamais rien (mesure : annonce 14,3 % pour un bruit vrai de
///     3,9 %), et personne ne s'en plaint, parce qu'un produit aveugle ne fait pas d'erreur visible.
/// </summary>
public class NoOpCalibrationTests
{
    private static NoOpObservation Obs(double delta, double resolution, double? focus = 1.0) =>
        new(DateTimeOffset.UnixEpoch, "zone calme", 10, 24, focus,
            GameMetricKind.Moyenne, delta, resolution);

    /// <summary>Une serie de controles dont les ecarts se REPARTISSENT jusqu'a
    /// <paramref name="ecartMax"/>, en alternant les signes.
    ///
    /// Premiere version de ce fixture : le meme ecart repete n fois. Le juge l'a refusee, et il
    /// avait raison — un ecart constant n'a pas de p95, donc la question « l'intervalle est-il trop
    /// large ? » n'avait pas de sens sur ces donnees. Un vrai no-op disperse.</summary>
    private static List<NoOpObservation> Serie(int n, double ecartMax, double resolution) =>
        Enumerable.Range(0, n)
            .Select(i => Obs(ecartMax * (i % 2 == 0 ? 1 : -1) * (0.55 + 0.45 * i / (double)n), resolution))
            .ToList();

    [Fact]
    public void Un_modele_qui_depasse_a_TOUS_LES_COUPS_est_declare_surconfiant()
    {
        // 20 controles, 20 depassements. Un intervalle a 95 % honnete en ferait environ un.
        var rapport = NoOpCalibration.Juger(Serie(20, ecartMax: 8.0, resolution: 2.0), GameMetricKind.Moyenne);

        rapport.Verdict.Should().Be(CalibrationVerdict.Surconfiant);
        rapport.Depassements.Should().Be(20);
    }

    [Fact]
    public void Un_modele_dont_l_intervalle_couvre_le_bruit_est_declare_calibre()
    {
        // Des ecarts qui montent jusqu'a 2,4 % pour une resolution annoncee de 2,5 % : jamais de
        // depassement, et l'intervalle n'est pas absurdement large non plus.
        var rapport = NoOpCalibration.Juger(Serie(30, ecartMax: 2.4, resolution: 2.5), GameMetricKind.Moyenne);

        rapport.Verdict.Should().Be(CalibrationVerdict.Calibre);
    }

    [Fact]
    public void Un_intervalle_TROIS_FOIS_TROP_LARGE_est_denonce_comme_de_l_aveuglement()
    {
        // Le defaut sous-confiant : jamais faux, jamais utile.
        var rapport = NoOpCalibration.Juger(Serie(30, ecartMax: 3.9, resolution: 14.3), GameMetricKind.Moyenne);

        rapport.Verdict.Should().Be(CalibrationVerdict.SousConfiant);
        rapport.Depassements.Should().Be(0, "il ne se trompe jamais — c'est bien le probleme");
    }

    [Fact]
    public void Sous_vingt_controles_le_juge_refuse_de_trancher()
    {
        // Meme des donnees catastrophiques ne suffisent pas : sur peu de tirages, un modele
        // honnete peut sortir n'importe quoi, et accuser sur trois points serait faire exactement
        // l'erreur qu'on cherche a empecher.
        var rapport = NoOpCalibration.Juger(Serie(5, ecartMax: 40.0, resolution: 1.0), GameMetricKind.Moyenne);

        rapport.Verdict.Should().Be(CalibrationVerdict.PasAssezDObservations);
    }

    [Fact]
    public void UN_SEUL_depassement_sur_vingt_est_exactement_ce_qu_on_attend()
    {
        // Le controle negatif du juge lui-meme : il ne doit pas crier au loup sur le taux nominal.
        var obs = Serie(19, ecartMax: 2.9, resolution: 3.0);
        obs.Add(Obs(delta: 5.0, resolution: 3.0));

        NoOpCalibration.Juger(obs, GameMetricKind.Moyenne)
            .Verdict.Should().Be(CalibrationVerdict.Calibre);
    }

    [Fact]
    public void Une_observation_prise_HORS_FOCUS_ne_calibre_rien()
    {
        // Elle ne mesure pas la machine en jeu : la compter reviendrait a calibrer le modele sur
        // le bruit d'un bureau Windows.
        var obs = Serie(25, ecartMax: 2.9, resolution: 3.0);
        obs.AddRange(Enumerable.Range(0, 25).Select(_ => Obs(delta: 90.0, resolution: 3.0, focus: 0.2)));

        var rapport = NoOpCalibration.Juger(obs, GameMetricKind.Moyenne);

        rapport.Observations.Should().Be(25);
        rapport.Verdict.Should().NotBe(CalibrationVerdict.Surconfiant);
    }

    // Une observation relue sans « ResolutionPct » vaut 0, et tout ecart non nul devenait un
    // depassement. Vingt-cinq lignes amputees suffisaient a declarer le modele surconfiant.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Une_observation_sans_resolution_annoncee_ne_calibre_rien(double resolution)
    {
        var obs = Serie(25, ecartMax: 2.9, resolution: 3.0);
        obs.AddRange(Enumerable.Range(0, 25).Select(_ => Obs(delta: 0.5, resolution: resolution)));

        var rapport = NoOpCalibration.Juger(obs, GameMetricKind.Moyenne);

        rapport.Observations.Should().Be(25);
        rapport.Verdict.Should().NotBe(CalibrationVerdict.Surconfiant);
    }

    [Fact]
    public void Chaque_grandeur_se_calibre_SEPAREMENT()
    {
        // Le 1 % low et la moyenne n'ont pas le meme bruit — c'est tout le sujet du sprint.
        // Les melanger dans une calibration commune reproduirait exactement l'erreur du score.
        var obs = Serie(25, ecartMax: 2.9, resolution: 3.0);
        obs.AddRange(Enumerable.Range(0, 25).Select(_ => new NoOpObservation(
            DateTimeOffset.UnixEpoch, "zone calme", 10, 24, 1.0,
            GameMetricKind.UnPourCentLow, 30.0, 3.0)));

        NoOpCalibration.Juger(obs, GameMetricKind.Moyenne)
            .Verdict.Should().Be(CalibrationVerdict.Calibre);
        NoOpCalibration.Juger(obs, GameMetricKind.UnPourCentLow)
            .Verdict.Should().Be(CalibrationVerdict.Surconfiant);
    }
}
