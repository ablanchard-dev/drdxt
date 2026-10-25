using DrDxt.Detection;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// Une minute comptée comme non jouée parce que le clavier n'a pas été lu et que la manette n'a pas bougé : une source non
// lue rend non lu, jamais « inactif ». Une entrée qui a bougé suffit à dire actif.
public sealed class EntreesDuJoueurCombineesTests
{
    [Theory]
    [InlineData(null, false, null)]
    [InlineData(false, null, null)]
    [InlineData(null, null, null)]
    [InlineData(null, true, true)]
    [InlineData(true, null, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void Les_entrees_se_combinent_sans_inventer_l_inactivite(bool? clavier, bool? manette, bool? attendu)
        => EntreesDuJoueur.Combiner(clavier, manette).Should().Be(attendu);
}
