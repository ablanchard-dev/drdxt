using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// L'image du serveur ne suffit pas : un programme non élevé du compte qui lance le vrai helper non élevé passe la garde
// d'image. Le client exige en plus que le tube appartienne aux Administrateurs ou à SYSTEM (un squatteur non élevé le crée
// à son propre SID et ne peut pas s'en donner un autre : Administrateurs est « deny-only » dans son jeton filtré), et que le
// jeton du serveur, s'il se lit, soit élevé. La traversée réelle (App moyenne → helper élevé) se mesure avec
// tools/helperhandshake ; ici, tout tourne à l'intégrité de l'hôte de test.
public sealed class PrivilegeDuServeurDuTubeTests
{
    private static string TubeUnique() => "DrDXT.Privilege." + Guid.NewGuid().ToString("N");

    private static bool HoteEleve()
    {
        using var moi = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(moi).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<string?> RefusFaceA(SecurityIdentifier proprietaire)
    {
        var tube = TubeUnique();
        var acl = new PipeSecurity();
        acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.SetOwner(proprietaire);
        using var serveur = NamedPipeServerStreamAcl.Create(tube, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, acl);
        var attente = serveur.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", tube, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await attente;
        return PipeServerIdentity.RefusDePrivilege(client);
    }

    [Fact]
    public async Task Un_tube_au_nom_d_un_compte_ordinaire_est_refuse()
    {
        var refus = await RefusFaceA(WindowsIdentity.GetCurrent().User!);

        refus.Should().NotBeNull().And.Contain("owned by S-1-5-21-", "le squatteur non élevé crée le tube à son propre SID");
    }

    [Fact]
    public async Task Un_tube_des_Administrateurs_servi_par_un_processus_eleve_passe()
    {
        if (!HoteEleve())
        {
            // Hôte non élevé : il ne peut pas donner le tube aux Administrateurs ; le refus du compte ordinaire reste prouvé.
            (await RefusFaceA(WindowsIdentity.GetCurrent().User!)).Should().NotBeNull();
            return;
        }
        var refus = await RefusFaceA(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        refus.Should().BeNull("témoin : tube des Administrateurs, serveur élevé, la garde laisse passer");
    }

    [Fact]
    public void Le_jeton_lisible_d_un_processus_non_eleve_le_dit()
        => PipeServerIdentity.JetonNonEleve((uint)Environment.ProcessId).Should().Be(!HoteEleve(),
            "le jeton de ce processus se lit : non élevé si et seulement si l'hôte ne l'est pas");
}
