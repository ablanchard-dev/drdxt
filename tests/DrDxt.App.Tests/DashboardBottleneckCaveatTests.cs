using DrDxt.App.Navigation;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// M8 — le CAVEAT de goulot d'etranglement.
///
/// Un gain FPS ne peut pas venir d'un reglage GPU si la session etait limitee par le CPU.
/// Sans ce caveat, le tableau de bord peut afficher « aucun gain mesurable » sans dire
/// Pourquoi, et l'utilisateur conclut que le produit ne sert a rien -- alors que la vraie
/// reponse est « cette session ne pouvait rien gagner cote GPU, tu etais CPU-bound ».
/// Le dire est de l'honnetete, et c'est aussi ce qui empeche de presenter un gain de banc
/// (ex. debit VRAM) comme un gain FPS en jeu.
///
/// Le signal existe depuis M8 (BenchmarkResult.Bottleneck, derive de GpuBusyPercent) mais
/// n'etait consomme nulle part cote App : il traversait le rapport sans jamais etre dit.
///
/// Seuils (decision produit documentee, pas une norme) : >= 90 % GPU-bound,
/// <= 75 % CPU/other-bound, la bande 75-90 reste Indeterminate pour ne jamais sur-affirmer.
/// </summary>
public sealed class DashboardBottleneckCaveatTests
{
    /// <summary>Un banc dont on connait le GPU-busy : c'est MeanGpuBusyMs qui pilote le
    /// signal (GpuBusyPercent = busy x AvgFps / 10).</summary>
    private static BenchmarkResult Bench(double avgFps, double? gpuBusyPercent)
    {
        // On inverse la formule pour poser le pourcentage voulu sans le fabriquer.
        double? meanBusy = gpuBusyPercent is { } pct && avgFps > 0 ? pct * 10.0 / avgFps : null;
        return new BenchmarkResult(avgFps, avgFps * 0.8, avgFps * 0.6,
            new[] { 5.0, 5.0 }, 15, 1.0, 3, meanBusy);
    }

    /// <summary>Construit le VM comme les tests voisins : vrai constructeur, seams substitues,
    /// localisateur identite (cle entrante = cle sortante) pour verifier le CABLAGE des cles,
    /// jamais la traduction.</summary>
    private static DashboardViewModel Build(ApplyReport report)
    {
        var vm = new DashboardViewModel(
            Substitute.For<ISafetyManager>(), Substitute.For<INavigationService>(),
            () => new object(), key => key);
        vm.Load(report);
        return vm;
    }

    private static DashboardViewModel VmFor(BenchmarkResult before, BenchmarkResult after,
        bool measurableGain, string summaryKey)
    {
        var verdict = new AbVerdict(before, after, measurableGain, summaryKey);
        return Build(new ApplyReport(new List<ApplyOutcome>(), verdict));
    }

    [Fact]
    public void CpuBoundSession_ShowsTheCaveat_SoNoGainIsNotReadAsProductFailure()
    {
        // 60 % de GPU-busy : le GPU attendait, c'est le CPU qui menait la session.
        var vm = VmFor(Bench(120, 60), Bench(120, 60), measurableGain: false, "verdict.no_measurable_gain");

        vm.ShowBottleneckCaveat.Should().BeTrue(
            "sans gain ET CPU-bound, il faut DIRE pourquoi : la session ne pouvait rien gagner cote GPU");
        vm.BottleneckCaveatKey.Should().Be("dashboard.caveat.cpu_bound");
    }

    [Fact]
    public void GpuBoundSession_ShowsNoCaveat_BecauseThereIsNothingToExcuse()
    {
        var vm = VmFor(Bench(120, 95), Bench(120, 95), measurableGain: false, "verdict.no_measurable_gain");

        vm.ShowBottleneckCaveat.Should().BeFalse(
            "GPU-bound : le reglage POUVAIT agir, l'absence de gain n'a pas d'excuse a donner");
    }

    [Fact]
    public void IndeterminateSession_ShowsNoCaveat_NeverAssertsALimiterItDidNotMeasure()
    {
        // 80 % tombe dans la bande 75-90 : on ne sait pas trancher, donc on se tait.
        var vm = VmFor(Bench(120, 80), Bench(120, 80), measurableGain: false, "verdict.no_measurable_gain");

        vm.ShowBottleneckCaveat.Should().BeFalse(
            "dans la bande indeterminee on n'affirme AUCUN limiteur -- UNKNOWN vaut mieux qu'invente");
    }

    [Fact]
    public void UnmeasuredGpuBusy_ShowsNoCaveat_NeverInventsALimiter()
    {
        var vm = VmFor(Bench(120, null), Bench(120, null), measurableGain: false, "verdict.no_measurable_gain");

        vm.ShowBottleneckCaveat.Should().BeFalse(
            "GPU-busy jamais mesure => aucun limiteur connu, donc rien a dire");
    }

    [Fact]
    public void CpuBoundSession_WithARealGain_StillShowsTheCaveat()
    {
        // Cas subtil et deliberе : meme avec un gain mesure, si la session etait CPU-bound il
        // faut le dire -- sinon on laisserait croire que le gain vient du reglage GPU alors
        // que le GPU n'etait pas le limiteur. C'est la protection anti-sur-attribution.
        var vm = VmFor(Bench(120, 60), Bench(140, 60), measurableGain: true, "verdict.measurable_gain");

        vm.ShowBottleneckCaveat.Should().BeTrue(
            "un gain obtenu sur une session CPU-bound ne doit pas etre attribue au reglage GPU");
    }

    [Fact]
    public void NoVerdictAtAll_ShowsNoCaveat_AndDoesNotCrash()
    {
        var vm = Build(new ApplyReport(new List<ApplyOutcome>(), Verdict: null));

        vm.ShowBottleneckCaveat.Should().BeFalse("sans verdict il n'y a aucun limiteur a nommer");
    }
}
