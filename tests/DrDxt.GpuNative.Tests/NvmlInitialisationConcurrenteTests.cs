using DrDxt.GpuNative.Nvml;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests.Nvml;

// -----------------------------------------------------------------------
// 23/09/2026. Trois scans de suite sur la RTX 4060 de la machine de reference : Resizable BAR lu, puis « non lu »,
// puis lu. Tracé : deux détections GPU tournent en même temps à chaque scan. `EnsureInit` posait
// son drapeau « déjà tenté » avant que nvmlInit ait répondu ; le second fil voyait le drapeau,
// sortait tout de suite et lisait `IsAvailable == false`. Le scan retenait tantôt l'une, tantôt
// l'autre détection : « non lu » dépendait du hasard.
//
// La règle : quel que soit le nombre de fils qui demandent en même temps, tous reçoivent la même
// réponse. Sur une machine sans NVIDIA la réponse commune est « non », et le test passe aussi :
// il ne prouve la course que là où NVML existe, ce qui est exactement là où elle mordait.
// -----------------------------------------------------------------------
public sealed class NvmlInitialisationConcurrenteTests
{
    [Fact]
    public void Tous_les_fils_qui_demandent_ensemble_recoivent_la_meme_reponse()
    {
        const int Fils = 8;
        for (var essai = 0; essai < 20; essai++)
        {
            var nvml = new NvmlNative();
            var depart = new Barrier(Fils);
            var reponses = new bool[Fils];
            var fils = Enumerable.Range(0, Fils).Select(i => new Thread(() =>
            {
                depart.SignalAndWait();
                reponses[i] = nvml.IsAvailable;
            })).ToList();
            fils.ForEach(f => f.Start());
            fils.ForEach(f => f.Join());

            reponses.Distinct().Should().ContainSingle(
                $"essai {essai} : un fil a lu « NVML absent » pendant qu'un autre l'initialisait");
        }
    }
}
