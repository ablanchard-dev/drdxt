using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Un test de vue échouait au hasard sous charge (« chemin 'Opacity' non résolu »). Cause : xUnit
// construit des vues sur plusieurs fils STA à la fois et le lecteur XAML de WPF ne le supporte pas. Seize
// fils, dix vues chacun : rouge trois fois sur trois avant que le banc ne rende une vue à la fois.
public sealed class VuesEnParalleleTests
{
    [Fact]
    public async Task Des_vues_construites_sur_plusieurs_fils_a_la_fois_ne_levent_rien()
    {
        int rendues = 0;
        var taches = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int n = 0; n < 10; n++)
            {
                var vm = ScanViewModelTestSeams.Build(
                    Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(),
                    Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
                StaApplication.Run(() =>
                {
                    vm.State = ScanState.Scanning;
                    var vue = new DrDxt.App.Views.ScanView { DataContext = vm };
                    vue.Measure(new System.Windows.Size(1240, 3000));
                    vue.Arrange(new System.Windows.Rect(0, 0, 1240, 3000));
                    vue.UpdateLayout();
                    Interlocked.Increment(ref rendues);
                    return 0;
                });
            }
        })).ToArray();

        await Task.WhenAll(taches);
        rendues.Should().Be(160, "chaque vue est allée jusqu'au rendu");
    }
}
