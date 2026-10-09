using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Quand une passe se perd, le banc en refait une — au lieu de rendre une mesure sans resolution.
///
/// `InterRunMarginOfError` vaut **+∞ sous trois passes utilisables**, et une passe qui ne rend
/// aucune trame exploitable ne compte pas. Le re-bench du moteur en demande exactement trois
/// (`ReBenchRuns = 3`) : **une seule passe perdue suffit** a rendre la resolution inconnue, donc a
/// interdire toute conclusion — y compris une regression qu'il aurait fallu annuler.
///
/// Ce n'est pas une hypothese : c'est une propriete arithmetique du code, lisible dans
/// `FrametimeStatistics.InterRunMarginOfError` (`if (runs.Count < 3) return PositiveInfinity`).
///
/// Le rattrapage est borne. Une machine ou toutes les passes se perdent ne doit pas mesurer
/// indefiniment : au bout du budget, le banc rend ce qu'il a, avec sa resolution inconnue et
/// assumee. Refaire une passe est une correction ; boucler serait un defaut.
/// </summary>
public class BenchmarkRunnerRecoversResolutionTests
{
    private static FrameCapture Capture(params double[] frames) =>
        new(frames, Array.Empty<double>(), Array.Empty<double>());

    /// <summary>Une session qui rend les captures programmees, dans l'ordre, et compte ses appels.</summary>
    private sealed class SessionScriptee : IPresentMonSession
    {
        private readonly Queue<Result<FrameCapture>> _reponses;
        public int Appels;

        public SessionScriptee(params Result<FrameCapture>[] reponses) => _reponses = new(reponses);

        public string? LastUnavailableDetail => null;
        public Result<bool> IsServiceAvailable() => Result<bool>.Ok(true);

        public Task<Result<FrameCapture>> CaptureAsync(int pid, TimeSpan duree, CancellationToken ct)
        {
            Appels++;
            // Une fois le script epuise, la machine se comporte bien : c'est ce qui permet de
            // distinguer « il a rattrape » de « il a boucle ».
            return Task.FromResult(_reponses.Count > 0
                ? _reponses.Dequeue()
                : Result<FrameCapture>.Ok(Capture(10.0, 10.0, 10.0)));
        }
    }

    private static Result<FrameCapture> Bonne() => Result<FrameCapture>.Ok(Capture(10.0, 10.1, 9.9, 10.0));

    /// <summary>Une capture qui aboutit mais ne rend rien d'exploitable : le cas qui fait
    /// disparaitre la resolution sans qu'aucune erreur ne soit levee.</summary>
    private static Result<FrameCapture> Vide() => Result<FrameCapture>.Ok(Capture(0.0, -1.0));

    [Fact]
    public async Task Une_passe_perdue_est_REFAITE_pour_que_la_resolution_existe()
    {
        var session = new SessionScriptee(Bonne(), Vide(), Bonne());

        var r = await new BenchmarkRunner(session).RunAsync(1234, TimeSpan.FromMilliseconds(10), 3, default);

        r.IsOk.Should().BeTrue();
        double.IsFinite(r.Value!.MarginOfError).Should().BeTrue(
            "sans rattrapage, deux passes utilisables donnent une marge infinie et aucune conclusion");
        session.Appels.Should().Be(4, "trois passes demandees, une perdue, une refaite");
    }

    [Fact]
    public async Task Une_mesure_qui_se_passe_bien_ne_relance_RIEN()
    {
        // Le rattrapage ne doit pas allonger une mesure saine : c'est du temps pris au joueur.
        var session = new SessionScriptee(Bonne(), Bonne(), Bonne());

        await new BenchmarkRunner(session).RunAsync(1234, TimeSpan.FromMilliseconds(10), 3, default);

        session.Appels.Should().Be(3);
    }

    [Fact]
    public async Task Le_rattrapage_est_BORNE_et_ne_boucle_jamais()
    {
        // Toutes les passes se perdent. Le banc doit s'arreter et assumer une resolution inconnue,
        // pas mesurer sans fin sur une machine ou la capture ne marche pas.
        var session = new SessionScriptee(
            Vide(), Vide(), Vide(), Vide(), Vide(), Vide(), Vide(), Vide(), Vide(), Vide());

        var r = await new BenchmarkRunner(session).RunAsync(1234, TimeSpan.FromMilliseconds(10), 3, default);

        session.Appels.Should().BeLessThanOrEqualTo(6, "trois passes demandees, trois rattrapages au plus");
        // Et le resultat reste HONNETE : soit un echec franc, soit une marge assumee inconnue.
        if (r.IsOk) double.IsFinite(r.Value!.MarginOfError).Should().BeFalse();
    }

    [Fact]
    public async Task Une_mesure_a_UNE_seule_passe_n_essaie_pas_d_en_inventer_trois()
    {
        // Demander une passe est un choix de l'appelant (un smoke rapide). Le banc ne doit pas
        // decider a sa place d'en faire trois : il rendrait une mesure que personne n'a demandee.
        var session = new SessionScriptee(Bonne());

        await new BenchmarkRunner(session).RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);

        session.Appels.Should().Be(1);
    }
}
