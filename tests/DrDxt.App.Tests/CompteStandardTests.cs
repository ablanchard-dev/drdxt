using DrDxt.App.Platform;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Sur un compte Windows standard, l'élévation par le mot de passe d'un administrateur lance le helper sous ce dernier : son
/// tube n'accepte que son compte, les Administrateurs et SYSTEM, et refuse l'App du joueur. Chaque écriture ouvrait une invite,
/// attendait 8 s, puis échouait. Le compte se reconnaît au démarrage ; toute écriture échoue tout de suite, avec sa raison.
/// </summary>
public sealed class CompteStandardTests
{
    // TOKEN_ELEVATION_TYPE (winnt.h) : 1 sans jeton lié, 2 élevé, 3 administrateur filtré par l'UAC.
    [Theory]
    [InlineData(1, false, true)]   // compte standard
    [InlineData(1, true, false)]   // administrateur, UAC coupé
    [InlineData(2, true, false)]   // administrateur élevé
    [InlineData(3, false, false)]  // administrateur sous l'UAC : le jeton filtré ne voit pas le groupe, le type le dit
    public void Seul_un_compte_sans_jeton_lie_et_hors_des_administrateurs_est_standard(int type, bool dansAdministrateurs, bool standard)
        => CompteDuJoueur.Standard(type, dansAdministrateurs).Should().Be(standard);

    // UAC coupé : un administrateur sans jeton lié tourne toujours élevé.
    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    public void L_UAC_coupe_se_reconnait(int type, bool dansAdministrateurs, bool coupe)
        => CompteDuJoueur.UacCoupe(type, dansAdministrateurs).Should().Be(coupe);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_compte_standard_se_dit_des_le_lancement(bool standard)
    {
        var vm = ScanViewModelTestSeams.Build(NSubstitute.Substitute.For<IHardwareDetector>(), NSubstitute.Substitute.For<ISettingsStateReader>(),
            NSubstitute.Substitute.For<IBenchmarkRunner>(), NSubstitute.Substitute.For<IScanScorer>(), NSubstitute.Substitute.For<IStorageDetector>(), k => k);
        vm.CompteStandard = () => standard;

        await vm.RendreSessionInterrompueAsync(NSubstitute.Substitute.For<DrDxt.Application.Autopilot.IRevertEnAttente>(),
            NSubstitute.Substitute.For<ITweakCatalog>());

        (vm.MessageDemarrage?.Contains("Demarrage.CompteStandard") == true).Should().Be(standard);
    }

    [Fact]
    public async Task Sur_un_compte_standard_une_ecriture_echoue_tout_de_suite_avec_sa_raison()
    {
        var client = ElevatedClientFactory.Create(compteStandard: true);
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        var r = await client.ExecuteAsync(new SetActivePowerPlanOp(Guid.Empty), CancellationToken.None);

        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "aucune invite, aucune attente");
        r.Error!.Value.Code.Should().Be(ErrorCodes.CompteStandard);
    }

    // L'App lancée sous un autre compte que celui de la session (« Exécuter en tant qu'administrateur » depuis un compte
    // standard) : son HKCU est la ruche de cet autre compte. Une valeur du compte n'y est pas écrite, elle est refusée.
    [Fact]
    public async Task Sous_un_autre_compte_que_la_session_une_valeur_du_compte_est_refusee()
    {
        var client = ElevatedClientFactory.Create(compteStandard: true, memeCompteQueLaSession: false);

        var r = await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\DrDxtTestHorsListe", "Rien", RegistryValueType.DWord, "0"), CancellationToken.None);

        r.IsOk.Should().BeFalse();
        // Hors de la liste blanche : sans la garde, la liste la refuserait avec un autre code, rien ne s'écrit.
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }
}
