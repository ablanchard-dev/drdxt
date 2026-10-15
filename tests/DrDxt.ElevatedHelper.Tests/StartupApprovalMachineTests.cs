using DrDxt.Core;
using FluentAssertions;
using Microsoft.Win32;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Couper puis réactiver un programme au démarrage sur la vraie session, par le jeton de l'App (HKCU), sans le helper. Le
/// programme est une entrée d'essai posée par le test lui-même ; aucun vrai programme n'est touché, et tout est effacé.
/// </summary>
public sealed class StartupApprovalMachineTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approuve = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Essai = "DrDxtEssaiDemarrage";

    private static byte[]? Octets()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Approuve);
        return k?.GetValue(Essai) as byte[];
    }

    [Fact]
    [Trait("Category", "Machine")] // écrit une entrée d'essai dans la vraie session, puis l'efface
    public async Task Une_entree_d_essai_se_coupe_et_se_reactive_comme_au_gestionnaire_des_taches()
    {
        var helper = Substitute.For<IElevatedClient>();
        var client = new ClientSessionUtilisateur(helper);
        using (var run = Registry.CurrentUser.CreateSubKey(Run, writable: true))
            run.SetValue(Essai, @"C:\Windows\System32\cmd.exe /c exit");
        try
        {
            (await client.ExecuteAsync(new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, Essai, false), default))
                .IsOk.Should().BeTrue();
            Octets().Should().NotBeNull().And.HaveCount(12);
            Octets()![0].Should().Be(3);

            (await client.ExecuteAsync(new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, Essai, true), default))
                .IsOk.Should().BeTrue();
            Octets()![0].Should().Be(2);
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            using (var run = Registry.CurrentUser.OpenSubKey(Run, writable: true)) run?.DeleteValue(Essai, throwOnMissingValue: false);
            using (var ok = Registry.CurrentUser.OpenSubKey(Approuve, writable: true)) ok?.DeleteValue(Essai, throwOnMissingValue: false);
        }
    }

    [Fact]
    public async Task Un_programme_qui_n_est_pas_liste_n_ecrit_rien()
    {
        var client = new ClientSessionUtilisateur(Substitute.For<IElevatedClient>());
        (await client.ExecuteAsync(new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "DrDxtAbsent" + Guid.NewGuid().ToString("N"), false), default))
            .IsOk.Should().BeFalse();
    }
}
