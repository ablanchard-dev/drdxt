using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// SEC2 (08/10/2026) : une valeur de la session de l'utilisateur (HKCU) s'écrit avec le jeton de l'App, jamais par le
/// helper administrateur. Élevé avec le compte d'un autre (UAC « par-dessus l'épaule »), le helper écrirait la ruche de cet
/// autre compte : le joueur croirait le mode Jeu allumé chez lui, il le serait chez l'administrateur.
/// </summary>
public sealed class ClientSessionUtilisateurTests
{
    private readonly IElevatedClient _helper = Substitute.For<IElevatedClient>();
    private readonly IRegistryActions _registre = Substitute.For<IRegistryActions>();

    private ClientSessionUtilisateur Client()
    {
        _helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _registre.SetValue(default, default!, default!, default, default!).ReturnsForAnyArgs(Result.Ok());
        _registre.DeleteValue(default, default!, default!).ReturnsForAnyArgs(Result.Ok());
        return new ClientSessionUtilisateur(_helper, _registre);
    }

    private static readonly SetRegistryValueOp ModeJeu =
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord, "1");

    [Fact]
    public async Task Une_valeur_HKCU_s_ecrit_par_l_App_jamais_par_le_helper()
    {
        (await Client().ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeTrue();

        _registre.Received(1).SetValue(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled",
            RegistryValueType.DWord, "1");
        await _helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Le_retour_HKCU_aussi()
    {
        await Client().ExecuteAsync(new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar",
            "AutoGameModeEnabled"), CancellationToken.None);

        _registre.Received(1).DeleteValue(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        await _helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Une_valeur_HKCU_hors_liste_blanche_est_refusee_et_rien_n_est_ecrit()
    {
        var r = await Client().ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Classes\exefile",
            "x", RegistryValueType.String, "y"), CancellationToken.None);

        r.IsOk.Should().BeFalse();
        _registre.DidNotReceiveWithAnyArgs().SetValue(default, default!, default!, default, default!);
    }

    // Revue de sécurité du 08/10 : régression de SEC2, les lectures n'atteignaient plus le helper.
    [Fact]
    public async Task Les_lectures_passent_au_helper()
    {
        var helper = Substitute.For<IElevatedClient>();
        helper.ReadAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<byte[]>.Ok([1, 2, 3])));
        var lu = await new ClientSessionUtilisateur(helper, _registre).ReadAsync(new PingOp(), CancellationToken.None);
        lu.IsOk.Should().BeTrue();
        lu.Value.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Le_reste_passe_au_helper()
    {
        var hklm = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "LowLatencyMode", RegistryValueType.DWord, "0");

        await Client().ExecuteAsync(hklm, CancellationToken.None);

        await _helper.Received(1).ExecuteAsync(hklm, Arg.Any<CancellationToken>());
        _registre.DidNotReceiveWithAnyArgs().SetValue(default, default!, default!, default, default!);
    }
}
