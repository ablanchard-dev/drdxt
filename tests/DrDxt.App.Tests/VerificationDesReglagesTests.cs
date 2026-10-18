using DrDxt.App.Platform;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La V1 relit au lancement ce que DrDXT a pose. Le manifeste porte un reglage garde : HKLM
// ...\Test\Valeur, pose a 1, origine 0. Le registre dit ce qui est vraiment la. Chemin reel : le vrai
// AppliedStateVerifier et la vraie regle PoseesDe ; seuls le manifeste et le registre sont simules.
public sealed class VerificationDesReglagesTests
{
    private const string Chemin = @"SOFTWARE\DrDXT\Test";
    private const string Nom = "Valeur";

    private static string Op(string valeur) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
        RegistryHiveKind.LocalMachine, Chemin, Nom, RegistryValueType.DWord, valeur)).Value!;

    private static async Task<IReadOnlyList<AppliedStateEvidence>> Relire(uint? valeurLue)
    {
        var garde = new AppliedTweak("latency.hagsOn", Op("1"), Op("0"), DateTimeOffset.UtcNow);
        var manifeste = Substitute.For<IDesiredStateStore>();
        manifeste.LoadAsync().Returns(Result<DesiredState>.Ok(
            new DesiredState(Array.Empty<AppliedTweak>(), new[] { garde })));
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, Chemin, Nom).Returns(Result<uint?>.Ok(valeurLue));
        return await VerificationDesReglages.VerifierAsync(manifeste, registre);
    }

    [Theory]
    [InlineData(1u, EtatApplique.Applied)]
    [InlineData(5u, EtatApplique.Drifted)]
    [InlineData(0u, EtatApplique.NotApplied)]
    public async Task Each_kept_setting_is_read_back_against_what_DrDXT_set(uint lu, EtatApplique attendu)
        => (await Relire(lu)).Single().Verdict.Should().Be(attendu);

    [Fact]
    public async Task A_setting_that_holds_says_nothing()
        => VerificationDesReglages.Dire(await Relire(1), k => k).Should().BeNull();

    [Fact]
    public async Task A_setting_changed_by_something_else_is_said_by_its_title()
    {
        // Le message traduit porte {0} : le titre du reglage doit y arriver, pas son id interne.
        static string Localiser(string cle) => cle == "reconciler.notify.user_drift" ? "modifie : {0}" : cle;

        VerificationDesReglages.Dire(await Relire(5), Localiser)
            .Should().Be("modifie : tweak.latency.hagsOn.title");
    }

    [Fact]
    public async Task An_unreadable_manifest_claims_nothing()
    {
        var manifeste = Substitute.For<IDesiredStateStore>();
        manifeste.LoadAsync().Returns(Result<DesiredState>.Fail(ErrorCodes.ManifestTampered, "falsifie"));

        (await VerificationDesReglages.VerifierAsync(manifeste, Substitute.For<IRegistryReader>()))
            .Should().BeEmpty();
    }
    // Le service du composite X3D gardé au manifeste se relit par son état au lancement : sans lecteur, Unavailable.
    [Fact]
    public async Task Un_service_garde_se_relit_par_son_etat()
    {
        static string Ser(ServiceAction a) => PrivilegedOpCodec.Serialize(new RunServiceControlOp("AMD3DVCacheSvc", a)).Value!;
        var manifeste = Substitute.For<IDesiredStateStore>();
        manifeste.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>(),
            new[] { new AppliedTweak("fixit.x3d_ccd", Ser(ServiceAction.Start), Ser(ServiceAction.Stop), DateTimeOffset.UtcNow) })));
        var services = Substitute.For<DrDxt.Application.Tweaks.Hardware.IServiceStateReader>();
        services.GetServiceState("AMD3DVCacheSvc").Returns(Result<DrDxt.Application.Tweaks.Hardware.ServiceState>.Ok(
            DrDxt.Application.Tweaks.Hardware.ServiceState.Running));

        (await VerificationDesReglages.VerifierAsync(manifeste, Substitute.For<IRegistryReader>(), services: services))
            .Single().Verdict.Should().Be(EtatApplique.Applied);
    }
}
