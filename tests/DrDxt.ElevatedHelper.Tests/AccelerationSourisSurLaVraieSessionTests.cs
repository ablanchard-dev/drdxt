using DrDxt.Core;
using FluentAssertions;
using Microsoft.Win32;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Les trois valeurs REG_SZ de l'accélération du pointeur (HKCU\Control Panel\Mouse) partent par le
/// jeton de l'App, sur la vraie ruche de la session, sans le helper ; chacune est lue avant, puis rendue à l'identique
/// (absente ⇒ supprimée), relue. Même preuve que SessionUtilisateurSurLaVraieRucheTests, sur le type REG_SZ.
/// </summary>
public sealed class AccelerationSourisSurLaVraieSessionTests
{
    private const string Chemin = @"Control Panel\Mouse";
    private static readonly string[] Noms = ["MouseSpeed", "MouseThreshold1", "MouseThreshold2"];

    private static object? Lire(string nom)
    {
        using var cle = Registry.CurrentUser.OpenSubKey(Chemin);
        return cle?.GetValue(nom);
    }

    [Fact]
    [Trait("Category", "Machine")] // écrit puis rend trois valeurs de la vraie session Windows
    public async Task Les_trois_valeurs_s_ecrivent_en_REG_SZ_et_se_rendent_a_l_identique()
    {
        var origines = Noms.ToDictionary(n => n, Lire);
        if (origines.Values.Any(o => o is not null and not string))
            return; // une origine d'un autre type serait réécrite en REG_SZ : rien à prouver ici
        var helper = Substitute.For<IElevatedClient>();
        var client = new ClientSessionUtilisateur(helper);
        var poses = new List<Result>();
        var lus = new Dictionary<string, object?>();
        try
        {
            foreach (var nom in Noms)
            {
                // Une valeur différente de l'origine, pour prouver l'écriture (l'origine « 0 » devient « 1 », et l'inverse).
                var cible = (string?)origines[nom] == "0" ? "1" : "0";
                poses.Add(await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom,
                    RegistryValueType.String, cible), CancellationToken.None));
                lus[nom] = Lire(nom);
            }
        }
        finally
        {
            foreach (var nom in Noms)
                _ = origines[nom] is string origine
                    ? await client.ExecuteAsync(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom,
                        RegistryValueType.String, origine), CancellationToken.None)
                    : await client.ExecuteAsync(new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom), CancellationToken.None);
        }
        poses.Should().OnlyContain(p => p.IsOk);
        foreach (var nom in Noms)
        {
            lus[nom].Should().Be((string?)origines[nom] == "0" ? "1" : "0", $"{nom} écrit");
            Lire(nom).Should().Be(origines[nom], $"{nom} rendu à l'identique");
        }
        await helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
}
