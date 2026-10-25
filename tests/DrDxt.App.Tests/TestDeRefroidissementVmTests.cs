using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Le test de refroidissement (avant : boucles PowerShell et sonde en administrateur, à la main) : un bouton dans l'App, sur la page du processeur. Le moteur (TestDeRefroidissement) est injecté :
// rien ne charge la machine ici.
public sealed class TestDeRefroidissementVmTests
{
    private sealed class Charge : IDisposable { public bool Arretee; public void Dispose() => Arretee = true; }

    private static (ScanViewModel Vm, Charge Charge) Vm(Func<int?> chaud, bool jeuOuvert = false)
    {
        var charge = new Charge();
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        vm.FabriqueTestRefroidissement = () => new TestDeRefroidissement(_ => charge,
            () => chaud() is { } c ? new EtatCpu(c, c - 15, c >= 100, false, false, false) : null, (_, ct) => Task.Delay(1, ct));
        vm.DureeDuTestRefroidissement = TimeSpan.FromSeconds(20);
        vm.JeuOuvertPourLeTest = () => jeuOuvert;
        vm.LimitePourLeTest = () => new LimiteDePuissanceLue(160, 160, 65, false);
        return (vm, charge);
    }

    [Fact]
    public async Task Le_test_se_lance_et_rend_le_verdict_du_moteur()
    {
        var (vm, charge) = Vm(() => 80);

        await vm.LancerTestRefroidissementCommand.ExecuteAsync(null);

        vm.TestRefroidissementEnCours.Should().BeFalse();
        vm.ResultatTestRefroidissement.Should().Contain("sans freinage").And.Contain("PL1 160 W");
        vm.TestRefroidissementEnDefaut.Should().BeFalse();
        charge.Arretee.Should().BeTrue("la charge s'arrête avec le test");
    }

    // Une panne de lecture qui lève ne sort pas de la commande : elle ferait tomber l'App en plein test.
    [Fact]
    public async Task Une_panne_pendant_le_test_se_dit_sans_lever()
    {
        var (vm, _) = Vm(() => 80);
        vm.LimitePourLeTest = () => throw new InvalidOperationException("lecture impossible");

        await vm.LancerTestRefroidissementCommand.ExecuteAsync(null);

        vm.TestRefroidissementEnCours.Should().BeFalse();
        // Le message technique part au journal ; l'écran dit la phrase générique.
        vm.ResultatTestRefroidissement.Should().Contain("Erreur.Generique").And.NotContain("lecture impossible");
    }

    [Fact]
    public async Task Un_arret_de_securite_se_dit_a_corriger()
    {
        var (vm, _) = Vm(() => 100);
        await vm.LancerTestRefroidissementCommand.ExecuteAsync(null);
        vm.TestRefroidissementEnDefaut.Should().BeTrue();
        vm.ResultatTestRefroidissement.Should().Contain("s'est arrêté seul");
    }

    [Fact]
    public async Task Jeu_ouvert_rien_ne_charge_et_c_est_dit()
    {
        var (vm, charge) = Vm(() => 80, jeuOuvert: true);
        await vm.LancerTestRefroidissementCommand.ExecuteAsync(null);
        vm.ResultatTestRefroidissement.Should().Be("Refroidissement.Test.JeuOuvert");
        charge.Arretee.Should().BeFalse("la charge n'a jamais démarré");
    }

    [Fact]
    public async Task Arreter_coupe_la_charge()
    {
        var lectures = 0;
        var (vm, charge) = Vm(() => { if (++lectures == 3) vmArreter?.Invoke(); return 80; });
        vmArreter = () => vm.ArreterTestRefroidissementCommand.Execute(null);
        await vm.LancerTestRefroidissementCommand.ExecuteAsync(null);
        charge.Arretee.Should().BeTrue();
        vm.TestRefroidissementEnCours.Should().BeFalse();
        lectures.Should().BeLessThan(10, "arrêté bien avant les 20 s");
    }

    private Action? vmArreter;
}
