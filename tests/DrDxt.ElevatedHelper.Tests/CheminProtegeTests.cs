using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Sur de vrais chemins de la machine, pas des ACL fabriquées.
public sealed class CheminProtegeTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-chemin-{Guid.NewGuid():N}");

    public CheminProtegeTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    [Fact]
    public void Un_executable_de_System32_est_protege()
        => CheminProtege.Verifier(Path.Combine(Environment.SystemDirectory, "sc.exe")).Should().BeNull();

    [Fact]
    public void Un_executable_sous_le_profil_n_est_pas_protege()
    {
        var exe = Path.Combine(_dossier, "PresentMonService.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        CheminProtege.Verifier(exe).Should().NotBeNull("le compte peut le remplacer : pas de démarrage en SYSTEM");
    }

    [Fact]
    public void Un_executable_sous_Program_Files_est_protege()
    {
        // L'installation par défaut de DrDXT vit sous Program Files : ce contrôle doit y passer.
        var exe = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
        if (!File.Exists(exe)) return;
        CheminProtege.Verifier(exe).Should().BeNull();
    }

    // Un ancêtre où le compte peut supprimer un enfant (son profil) laisse renommer le dossier protégé et en poser un autre.
    // Créer un dossier neuf, comme C:\ le permet aux utilisateurs, ne remplace rien.
    [Theory]
    [InlineData(System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, false)]
    [InlineData(System.Security.AccessControl.FileSystemRights.Delete, false)]
    [InlineData(System.Security.AccessControl.FileSystemRights.ChangePermissions, false)]
    [InlineData(System.Security.AccessControl.FileSystemRights.FullControl, false)]
    [InlineData(System.Security.AccessControl.FileSystemRights.AppendData, true)]
    [InlineData(System.Security.AccessControl.FileSystemRights.ReadAndExecute, true)]
    public void Un_ancetre_ne_laisse_pas_le_compte_remplacer_ce_qu_il_contient(
        System.Security.AccessControl.FileSystemRights droitDuCompte, bool protege)
    {
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            droitDuCompte, System.Security.AccessControl.AccessControlType.Allow));

        (CheminProtege.ProblemeAncetre(acl, @"C:\Users\compte") is null).Should().Be(protege);
    }

    // Une règle effective peut porter un masque générique non mappé : .NET refuse de le construire, mais le relit d'une vraie
    // ACL. GENERIC_ALL vaut le contrôle total ; GENERIC_WRITE, mappé sur FILE_GENERIC_WRITE, ne porte ni suppression ni DACL.
    [Theory]
    [InlineData("GA", false)]
    [InlineData("GW", true)]
    public void Un_masque_generique_compte_comme_les_droits_qu_il_donne(string masque, bool protege)
    {
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm($"O:SYD:(A;;{masque};;;AU)");

        (CheminProtege.ProblemeAncetre(acl, @"C:\Users\compte") is null).Should().Be(protege);
    }

    [Fact]
    public void Un_dossier_verrouille_sous_un_dossier_du_compte_n_est_pas_protege()
    {
        // Verrouiller un dossier aux administrateurs demande l'élévation ; sans elle, rien à prouver ici.
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return;
        var verrouille = Directory.CreateDirectory(Path.Combine(_dossier, "x"));
        var exe = new FileInfo(Path.Combine(verrouille.FullName, "PresentMonService.exe"));
        File.WriteAllBytes(exe.FullName, [0x4D, 0x5A]);
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var systeme = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        void Verrouiller(FileSystemInfo f, System.Security.AccessControl.FileSystemSecurity acl)
        {
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (System.Security.AccessControl.FileSystemAccessRule r in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                acl.RemoveAccessRuleSpecific(r);
            acl.SetOwner(admins);
            acl.AddAccessRule(new(admins, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new(systeme, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        }
        var aclExe = exe.GetAccessControl(); Verrouiller(exe, aclExe); exe.SetAccessControl(aclExe);
        var aclDossier = verrouille.GetAccessControl(); Verrouiller(verrouille, aclDossier); verrouille.SetAccessControl(aclDossier);

        CheminProtege.Verifier(Path.Combine(_dossier, "..", Path.GetFileName(_dossier), "x", "PresentMonService.exe"))
            .Should().NotBeNull("le compte peut renommer x depuis le dossier au-dessus et poser son propre binaire");
        CheminProtege.ProblemeAncetre(new DirectoryInfo(_dossier).GetAccessControl(), _dossier)
            .Should().NotBeNull("témoin : c'est l'ancêtre qui ouvre la faille");
    }

    [Fact]
    public void Un_ancetre_dont_le_compte_est_proprietaire_n_est_pas_sur()
    {
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        CheminProtege.ProblemeAncetre(acl, @"C:\Users\compte").Should().NotBeNull("un propriétaire réécrit la DACL");
    }

    [Fact]
    public void Un_executable_absent_n_est_pas_une_preuve()
        => CheminProtege.Verifier(Path.Combine(_dossier, "absent.exe")).Should().NotBeNull();
}
