using DrDxt.Core;
using FluentAssertions;
using Microsoft.Win32;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// SEC2 / ADM5 (08/10/2026) : la valeur HKCU part par le jeton de l'App, sur la vraie ruche de la session, sans jamais
/// toucher le helper. Valeur choisie : le mode Jeu automatique à 1, qui est le défaut de Windows. L'état d'origine est lu
/// avant, et rendu à la fin (valeur absente ⇒ supprimée, présente ⇒ réécrite), relu.
/// Revue du 08/10/2026 (REV5) : une origine déjà à 1 ne prouverait rien, une origine d'un autre type serait réécrite en
/// DWORD : dans ces deux cas le test ne touche à rien et le dit ; et l'échec de l'écriture n'est plus masqué par le rendu.
/// </summary>
public sealed class SessionUtilisateurSurLaVraieRucheTests
{
    private const string Chemin = @"Software\Microsoft\GameBar";
    private const string Nom = "AutoGameModeEnabled";

    private static object? Lire()
    {
        using var cle = Registry.CurrentUser.OpenSubKey(Chemin);
        return cle?.GetValue(Nom);
    }

    [Fact]
    [Trait("Category", "Machine")] // écrit puis rend une valeur de la vraie session Windows
    public async Task L_App_ecrit_et_rend_la_valeur_de_session_sans_le_helper()
    {
        var origine = Lire();
        if (origine is not null && (origine is not int || (int)origine == 1))
            return; // rien à prouver sans changer le type, ou déjà à 1

        var helper = Substitute.For<IElevatedClient>();
        var client = new ClientSessionUtilisateur(helper);
        Result pose;
        object? lu;
        try
        {
            pose = await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, Nom,
                RegistryValueType.DWord, "1"), CancellationToken.None);
            lu = Lire();
        }
        finally
        {
            _ = origine is null
                ? await client.ExecuteAsync(new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, Nom), CancellationToken.None)
                : await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, Nom,
                    RegistryValueType.DWord, ((int)origine).ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None);
        }

        pose.IsOk.Should().BeTrue(pose.Error?.Message);
        lu.Should().Be(1);
        Lire().Should().Be(origine, "l'origine est rendue");
        await helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
}
