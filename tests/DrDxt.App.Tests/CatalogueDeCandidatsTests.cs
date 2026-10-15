using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

/// <summary>
/// Le catalogue des candidats et les leviers reellement enregistres ne peuvent pas diverger.
/// Un levier ajoute sans descripteur ne serait jamais tente par l'App ; un levier dont la nature
/// ment ferait mesurer un effet qui n'existe qu'apres redemarrage.
/// </summary>
public sealed class CatalogueDeCandidatsTests
{
    [Fact]
    public void Chaque_levier_du_produit_est_decrit_et_sa_nature_est_la_sienne()
    {
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        var leviers = provider.GetServices<ITweak>().ToList();
        leviers.Should().NotBeEmpty();

        foreach (var t in leviers)
        {
            var d = CatalogueDeCandidats.Decrire(t.Id);
            d.Should().NotBeNull($"« {t.Id} » est enregistre mais absent du catalogue");
            if (d!.Kind is CandidateKind.AutoHot or CandidateKind.AutoReboot)
                d.Kind.Should().Be(t.TakesEffectAfterReboot ? CandidateKind.AutoReboot : CandidateKind.AutoHot, t.Id);
        }
    }
}
