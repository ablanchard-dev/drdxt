using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// Un incident passager à l'ouverture du jeu anglais ne condamne pas le lecteur jusqu'au redémarrage de l'application.
[Collection(LaTableDesCompteursNeSeRelitPasAChaqueNomTests.NomDeLaCollection)]
public sealed class UnEchecDOuvertureNEstPasRetenuTests : IDisposable
{
    private readonly Func<bool> _source = CpuFrequencyReader.AnglaisExiste;

    public void Dispose()
    {
        CpuFrequencyReader.AnglaisExiste = _source;
        CpuFrequencyReader.OublierLesTables();
    }

    [Fact]
    public void Une_exception_n_est_pas_retenue_une_reponse_l_est()
    {
        CpuFrequencyReader.OublierLesTables();
        var appels = 0;
        CpuFrequencyReader.AnglaisExiste = () => ++appels == 1 ? throw new InvalidOperationException() : true;

        CpuFrequencyReader.AnglaisOuvrable().Should().BeFalse("cette fois, l'ouverture a échoué");
        CpuFrequencyReader.AnglaisOuvrable().Should().BeTrue("l'échec n'a pas été retenu");
        CpuFrequencyReader.AnglaisOuvrable().Should().BeTrue();
        appels.Should().Be(2, "la réponse, elle, est retenue");
    }
}
