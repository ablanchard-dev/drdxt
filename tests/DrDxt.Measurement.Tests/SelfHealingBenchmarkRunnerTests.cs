using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// `InstallPresentMonServiceOp` est ecrite, allowlistee, testee — et personne ne l'envoie.
///
/// Le service de mesure se demarrait a la main sur la machine de reference. le plan de l'installeur (non publie) affirme
/// pourtant depuis des mois que « l'app demarre le service quand une mesure est demandee » : c'est
/// la phrase qui a couvert le trou. Resultat concret : DrDXT installe avec un ancien binPath
/// garde le tube par defaut, celui du service Intel, et aucun scan ne peut le reparer.
///
/// Ce decorateur est le seul chemin par lequel le produit se repare lui-meme. Il tient en une
/// regle : **un seul echec justifie une elevation** -- le service qui ne repond pas. Ni l'absence
/// de jeu, ni un jeu protege, ni une capture vide : demander l'UAC pour ces cas-la serait du bruit.
/// </summary>
public class SelfHealingBenchmarkRunnerTests
{
    private static readonly BenchmarkResult Mesure =
        new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    /// <summary>Un banc qui rend les resultats programmes, dans l'ordre, et compte ses appels.</summary>
    private sealed class BancScripte : IBenchmarkRunner
    {
        private readonly Queue<Result<BenchmarkResult>> _reponses;
        public int Appels;

        public BancScripte(params Result<BenchmarkResult>[] reponses) => _reponses = new(reponses);

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan duree, int runs, CancellationToken ct)
        {
            Appels++;
            return Task.FromResult(_reponses.Count > 0
                ? _reponses.Dequeue()
                : Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, "script epuise"));
        }
    }

    private sealed class HelperFake : IElevatedClient
    {
        private readonly Result _reponse;
        public readonly List<PrivilegedOp> Recues = new();

        public HelperFake(Result? reponse = null) => _reponse = reponse ?? Result.Ok();

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recues.Add(op);
            return Task.FromResult(_reponse);
        }
    }

    private static Task<Result<BenchmarkResult>> Lancer(IBenchmarkRunner interne, IElevatedClient helper)
        => new SelfHealingBenchmarkRunner(interne, helper)
            .RunAsync(1234, TimeSpan.FromSeconds(1), 1, CancellationToken.None);

    [Fact]
    public async Task Service_muet_le_produit_INSTALLE_le_service_et_remesure()
    {
        var banc = new BancScripte(
            Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "service indisponible"),
            Result<BenchmarkResult>.Ok(Mesure));
        var helper = new HelperFake();

        var r = await Lancer(banc, helper);

        helper.Recues.Should().ContainSingle().Which.Should().BeOfType<InstallPresentMonServiceOp>();
        banc.Appels.Should().Be(2);
        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(Mesure);
    }

    [Fact]
    public async Task Une_mesure_qui_marche_ne_demande_JAMAIS_l_elevation()
    {
        var banc = new BancScripte(Result<BenchmarkResult>.Ok(Mesure));
        var helper = new HelperFake();

        (await Lancer(banc, helper)).IsOk.Should().BeTrue();

        helper.Recues.Should().BeEmpty("un scan qui reussit n'a aucune raison de reveiller l'UAC");
        banc.Appels.Should().Be(1);
    }

    [Fact]
    public async Task Aucun_jeu_a_mesurer_n_est_PAS_une_raison_d_elever()
    {
        // Installer un service ne fait pas apparaitre un jeu. Elever ici serait du bruit pur.
        var banc = new BancScripte(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "pas de jeu"));
        var helper = new HelperFake();

        var r = await Lancer(banc, helper);

        helper.Recues.Should().BeEmpty();
        banc.Appels.Should().Be(1);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PidInvalid);
    }

    [Fact]
    public async Task La_reparation_n_est_tentee_QU_UNE_fois()
    {
        // Sans cette borne, un service durablement casse ferait boucler l'UAC a chaque scan.
        var banc = new BancScripte(
            Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "1er"),
            Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "2e"));
        var helper = new HelperFake();

        var r = await Lancer(banc, helper);

        helper.Recues.Should().HaveCount(1);
        banc.Appels.Should().Be(2);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.PmonUnavailable);
    }

    [Fact]
    public void Le_conteneur_rend_le_banc_QUI_SAIT_SE_REPARER()
    {
        // La garde qui manquait. Un decorateur ecrit et non cable, c'est exactement l'etat
        // dans lequel `InstallPresentMonServiceOp` a passe des mois : parfaitement teste, jamais
        // atteint. Ce test echoue si quelqu'un remet `TryAddSingleton<IBenchmarkRunner, BenchmarkRunner>`.
        var services = new ServiceCollection();
        services.AddSingleton<IElevatedClient>(new HelperFake());
        services.AddDrDxtMeasurement();

        services.BuildServiceProvider().GetRequiredService<IBenchmarkRunner>()
            .Should().BeOfType<DrDxt.Measurement.Frames.RepliDxgiBenchmarkRunner>()
            .Which.PresentMon.Should().BeOfType<SelfHealingBenchmarkRunner>();
    }

    [Fact]
    public async Task UAC_refuse_on_rend_l_echec_d_origine_sans_remesurer()
    {
        // L'utilisateur a dit non. Remesurer donnerait exactement le meme echec, plus lentement.
        var banc = new BancScripte(Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "cause d'origine"));
        var helper = new HelperFake(Result.Fail(ErrorCodes.HelperOpFailed, "eleve refuse"));

        var r = await Lancer(banc, helper);

        banc.Appels.Should().Be(1);
        r.Error!.Value.Code.Should().Be(ErrorCodes.PmonUnavailable);
        r.Error!.Value.Message.Should().Contain("cause d'origine",
            "le message rendu doit rester celui de la mesure, pas celui du helper");
    }
}
