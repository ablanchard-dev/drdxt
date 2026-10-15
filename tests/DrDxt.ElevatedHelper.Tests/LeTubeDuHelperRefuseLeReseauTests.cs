using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// La source de .NET (NamedPipeServerStream.Windows.cs, dotnet/runtime) ne pose pas
/// PIPE_REJECT_REMOTE_CLIENTS : sans autre barriere, le tube du helper accepte une session reseau
/// (SMB) qui passe son ACL. Une ACE de refus pour NETWORK (S-1-5-2) la ferme en amont ; un refus est
/// evalue avant les autorisations. Le tour qui l'avait mesure a montre qu'elle ne gene pas le local.
/// </summary>
public sealed class LeTubeDuHelperRefuseLeReseauTests
{
    private static List<PipeAccessRule> Regles() =>
        PipeSecurityFactory.CreateRestrictiveSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();

    [Fact]
    public void L_acl_refuse_le_reseau_et_n_ouvre_rien_a_tout_le_monde()
    {
        var reseau = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var regles = Regles();

        regles.Should().Contain(r => r.IdentityReference.Equals(reseau) && r.AccessControlType == AccessControlType.Deny);
        regles.Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference)
            .Should().NotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null))
            .And.NotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        PipeSecurityFactory.CreateRestrictiveSecurity().AreAccessRulesProtected.Should().BeTrue();
    }

    [Fact]
    public async Task Le_refus_du_reseau_ne_gene_pas_une_connexion_locale()
    {
        var nom = $"drdxt-test-{Guid.NewGuid():N}";
        using var serveur = PipeSecurityFactory.CreateServerStream(nom);
        var attente = serveur.WaitForConnectionAsync();

        using var client = new NamedPipeClientStream(".", nom, PipeDirection.InOut);
        await client.ConnectAsync(5_000);
        await attente;

        serveur.IsConnected.Should().BeTrue();
    }
}
