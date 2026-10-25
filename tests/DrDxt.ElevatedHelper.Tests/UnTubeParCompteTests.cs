using System.Security.Principal;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Deux comptes Windows ouverts en même temps (changement rapide d'utilisateur) : avec un seul nom de tube, le helper du
// second ne pouvait pas créer le sien (FirstPipeInstance) tant que celui du premier vivait, et tous ses gestes élevés
// échouaient. L'App et son helper partagent le SID du compte, élevé ou non : le nom se dérive de lui.
public sealed class UnTubeParCompteTests
{
    private static readonly SecurityIdentifier Alice = new("S-1-5-21-1000000001-1000000002-1000000003-1001");
    private static readonly SecurityIdentifier Bob = new("S-1-5-21-1000000001-1000000002-1000000003-1002");

    [Fact]
    public void Deux_comptes_ont_deux_tubes_et_un_compte_toujours_le_meme()
    {
        PipeSecurityFactory.NomPourLeCompte(Alice).Should().NotBe(PipeSecurityFactory.NomPourLeCompte(Bob));
        PipeSecurityFactory.NomPourLeCompte(Alice).Should().Be(PipeSecurityFactory.NomPourLeCompte(new SecurityIdentifier(Alice.Value)));
    }

    [Fact]
    public void Le_nom_par_defaut_est_celui_du_compte_courant()
    {
        PipeSecurityFactory.DefaultPipeName.Should().Be(PipeSecurityFactory.NomPourLeCompte(WindowsIdentity.GetCurrent().User))
            .And.StartWith(PipeSecurityFactory.PrefixeDuTube + ".");
    }
}
