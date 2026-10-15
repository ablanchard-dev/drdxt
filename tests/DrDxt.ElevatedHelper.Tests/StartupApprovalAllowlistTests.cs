using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Couper ou réactiver un programme au démarrage : trois listes, deux ruches, un nom simple. La sécurité de Windows
// ne se coupe pas.
public sealed class StartupApprovalAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    [Theory]
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "Discord", false)]
    [InlineData(RegistryHiveKind.LocalMachine, SetStartupApprovalOp.Run, "RtkAudUService", false)]
    [InlineData(RegistryHiveKind.LocalMachine, SetStartupApprovalOp.Run32, "Steam", false)]
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.DossierDemarrage, "Spotify.lnk", false)]
    [InlineData(RegistryHiveKind.LocalMachine, SetStartupApprovalOp.Run, "SecurityHealth", true)]
    public void Les_gestes_du_gestionnaire_des_taches_passent(RegistryHiveKind ruche, string liste, string nom, bool actif)
        => _allow.Validate(new SetStartupApprovalOp(ruche, liste, nom, actif)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(RegistryHiveKind.LocalMachine, SetStartupApprovalOp.Run, "SecurityHealth", false)] // la sécurité de Windows reste
    [InlineData(RegistryHiveKind.LocalMachine, "RunOnce", "x", false)]                          // liste hors de l'ensemble
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run32, "x", false)]           // Run32 n'existe qu'en machine
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "", false)]
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, @"..\Policies", false)]
    [InlineData(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "a/b", false)]
    public void Le_reste_est_refuse(RegistryHiveKind ruche, string liste, string nom, bool actif)
        => _allow.Validate(new SetStartupApprovalOp(ruche, liste, nom, actif)).IsOk.Should().BeFalse();

    [Fact]
    public void Une_autre_ruche_est_refusee()
        => _allow.Validate(new SetStartupApprovalOp((RegistryHiveKind)99, SetStartupApprovalOp.Run, "x", false)).IsOk.Should().BeFalse();
}
