using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Un chemin de service non cité et contenant des espaces fait essayer à Windows, dans
/// l'ordre, chaque préfixe suivi de « .exe ». Ce n'est exploitable que si l'un de ces emplacements accepte
/// un fichier : sur une machine saine (C:\ n'accorde que la création de dossiers), l'audit se tait.
/// </summary>
public sealed class CheminsNonCitesTests
{
    [Fact]
    public void Chaque_espace_avant_le_binaire_donne_un_emplacement_essaye()
        => RepairAuditService.CandidatsNonCites(@"C:\Program Files (x86)\Common Files\Reallusion\Svc.exe -x")
            .Should().Equal(@"C:\Program.exe", @"C:\Program Files.exe", @"C:\Program Files (x86)\Common.exe");

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\svc.exe\"")]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs")]
    [InlineData(null)]
    public void Un_chemin_cite_ou_sans_espace_n_essaie_rien(string? pathName)
    {
        // Témoin : le même calcul rend bien quelque chose sur un chemin non cité avec espace.
        RepairAuditService.CandidatsNonCites(@"C:\A B\x.exe").Should().NotBeEmpty();
        RepairAuditService.CandidatsNonCites(pathName).Should().BeEmpty();
    }

    [Fact]
    public void Un_dossier_du_compte_accepte_un_fichier_et_Windows_non()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-noncite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            RepairAuditService.DossierAccepteUnFichier(dossier).Should().BeTrue("le compte en est propriétaire");
            RepairAuditService.DossierAccepteUnFichier(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .Should().BeFalse();
        }
        finally { Directory.Delete(dossier); }
    }

    [Fact]
    public void La_racine_du_disque_systeme_n_accepte_qu_un_dossier_donc_rien_a_signaler()
        // Mesuré le 25/09/2026 : C:\ accorde « AppendData » (créer un dossier) aux utilisateurs
        // authentifiés, pas « WriteData » (créer un fichier). C:\Program.exe ne peut pas y être posé.
        => RepairAuditService.DossierAccepteUnFichier(Path.GetPathRoot(Environment.SystemDirectory)!).Should().BeFalse();
}
