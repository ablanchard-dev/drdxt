using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// La surface declaree est celle que le levier ecrit.
///
/// Le veto des arrets brutaux laisse passer deux surfaces : l'OS utilisateur et « rien ». Ce sont
/// donc les seules declarations qui doivent etre prouvees — une surface fermee declaree a tort ne
/// fait que refuser de trop. Chaque levier reellement enregistre qui se dit OS utilisateur est
/// execute sur des doublures (aucune ecriture machine : le client eleve enregistre et repond Ok),
/// et chaque operation qu'il envoie doit viser HKCU. Un levier du pilote re-etiquete « OS » envoie
/// une ecriture HKLM ou NVAPI, et ce test rougit.
/// </summary>
public sealed class SurfaceDesLeviersTests
{
    private sealed class ClientQuiEnregistre : IElevatedClient
    {
        public List<PrivilegedOp> Envoyees { get; } = new();

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Envoyees.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    /// <summary>La lecture independante : ne passe le veto que ce qui ecrit la session utilisateur.</summary>
    private static bool VisePreferencesUtilisateur(PrivilegedOp op) => op
        is SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser }
        or DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser };

    private static (ServiceProvider Provider, ClientQuiEnregistre Client) ProduitSurDoublures()
    {
        var client = new ClientQuiEnregistre();

        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(default, default!, default!).ReturnsForAnyArgs(Result<uint?>.Ok(0u));
        registre.ReadString(default, default!, default!).ReturnsForAnyArgs(Result<string?>.Ok(null));
        registre.ReadMultiString(default, default!, default!).ReturnsForAnyArgs(Result<string?>.Ok(null));

        var nvApi = Substitute.For<INvApi>();
        nvApi.IsAvailable.Returns(true);

        // Enregistrees apres le produit : la derniere inscription l'emporte.
        var services = new ServiceCollection().AddDrDxtApp();
        services.AddSingleton<IElevatedClient>(client);
        services.AddSingleton(registre);
        services.AddSingleton(nvApi);
        return (services.BuildServiceProvider(), client);
    }

    [Fact]
    public async Task Chaque_levier_qui_se_dit_OS_utilisateur_n_ecrit_QUE_la_session_utilisateur()
    {
        var (provider, client) = ProduitSurDoublures();
        using var _ = provider;
        var prouves = new List<string>();

        foreach (var levier in provider.GetServices<ITweak>())
        {
            var d = CatalogueDeCandidats.Decrire(levier.Id);
            d.Should().NotBeNull($"« {levier.Id} » est enregistre mais absent du catalogue");
            if (d!.Surface is not (SurfaceTouchee.OsUtilisateur or SurfaceTouchee.Aucune)) continue;

            client.Envoyees.Clear();
            var r = await levier.ApplyAsync();

            if (d.Surface == SurfaceTouchee.Aucune)
            {
                client.Envoyees.Should().BeEmpty($"« {levier.Id} » se dit sans ecriture");
            }
            else
            {
                r.IsOk.Should().BeTrue($"« {levier.Id} » doit s'executer pour prouver sa surface ({r.Error?.Message})");
                client.Envoyees.Should().NotBeEmpty($"« {levier.Id} » : une surface OS se prouve par ce qui est ecrit");
                client.Envoyees.Should().OnlyContain(op => VisePreferencesUtilisateur(op),
                    $"« {levier.Id} » se dit OS utilisateur : il ne doit ecrire ni HKLM, ni la carte, ni le pilote");
            }
            prouves.Add(levier.Id);
        }

        prouves.Should().BeEquivalentTo(new[]
        {
            "latency.gameModeOn", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn", "latency.mouse_acceleration_off",
        }, "la liste des surfaces ouvertes est courte, et elle se relit ici");
    }
}
