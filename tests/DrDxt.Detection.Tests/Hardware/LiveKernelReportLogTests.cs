using System.Text;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Deux facons de casser ce lecteur sans que rien ne leve.
///
/// 1. Lire le code d'arret en DECIMAL. `Sig[0].Value=193` deviendrait 0xC1 au lieu de 0x193 : le
///    lecteur rendrait toujours un rapport, avec un code plausible, et le nom disparaitrait
///    silencieusement de la table. Un gardien du noyau graphique passerait pour un code inconnu.
///
/// 2. Chercher les champs par leur NOM. `Report.wer` est localise — sur le Windows francais de la
///    machine de reference le champ s'appelle `Sig[1].Name=Paramètre 1`. Un lecteur qui cherche
///    « Parameter 1 » rendrait `null` ici, et un lecteur qui cherche « Paramètre 1 » rendrait
///    `null` partout ailleurs. Dans les deux cas : aucune exception, une donnee manquante.
///
/// Les fixtures ci-dessous sont ecrites en UTF-16 avec les noms de champs FRANCAIS, comme les
/// vrais fichiers de cette machine.
/// </summary>
public class LiveKernelReportLogTests : IDisposable
{
    private readonly string _racine = Path.Combine(
        Path.GetTempPath(), "drdxt-wer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_racine)) Directory.Delete(_racine, recursive: true);
    }

    /// <summary>Ecrit un `Report.wer` conforme a ceux du 08/09/2026 : UTF-16, champs localises.</summary>
    private string Rapport(string dossier, string type, string code, string parametre1,
        long filetime, string? vidage = null)
    {
        var chemin = Path.Combine(_racine, dossier);
        Directory.CreateDirectory(chemin);
        File.WriteAllText(Path.Combine(chemin, "Report.wer"),
            $"""
            EventType={type}
            EventTime={filetime}
            Sig[0].Name=Code
            Sig[0].Value={code}
            Sig[1].Name=Paramètre 1
            Sig[1].Value={parametre1}
            AppName=Windows
            """, Encoding.Unicode);
        if (vidage is not null) File.WriteAllText(Path.Combine(chemin, vidage), "x");
        return chemin;
    }

    // 06/10/2026 (chantier B8c) : sans droits administrateur, chaque dossier `Kernel_*` est lisible par les seuls
    // administrateurs (ACL relue sur la machine de référence) et `File.Exists` y répond « absent ». Le lecteur rendait
    // « aucun rapport » sur une machine qui en avait deux.
    [Fact]
    public void Un_rapport_present_mais_refuse_se_dit_illisible_pas_absent()
    {
        var dossier = new DirectoryInfo(Rapport("Kernel_193_abc", "LiveKernelEvent", "193", "80e", 134318324727177547));
        var wer = new FileInfo(Path.Combine(dossier.FullName, "Report.wer"));
        var moi = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var refus = new System.Security.AccessControl.FileSystemAccessRule(moi,
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Deny);
        var aclFichier = wer.GetAccessControl();
        aclFichier.AddAccessRule(refus);
        wer.SetAccessControl(aclFichier);
        var aclDossier = dossier.GetAccessControl();
        aclDossier.AddAccessRule(refus);
        dossier.SetAccessControl(aclDossier);
        try
        {
            // Pas de précontrôle sur File.Exists : selon le compte qui lance les tests (privilèges de l'administrateur,
            // runner de CI), Windows rend ou non les attributs d'un fichier refusé. Le lecteur n'utilise plus File.Exists ;
            // ce qui est prouvé ici, c'est qu'une ouverture refusée est dite, jamais lue comme une absence.
            var r = new WerLiveKernelReportLog(_racine).Lire();

            r.IsOk.Should().BeFalse("un rapport refusé n'est pas une absence de rapport");
            r.Error!.Value.Message.Should().Contain("accès refusé");

            // Revue (F-31) : un second rapport, lisible : il est rendu, le refusé est compté à part.
            Rapport("Kernel_144_def", "LiveKernelEvent", "144", "3003", 134318324727177547);
            var partiel = new WerLiveKernelReportLog(_racine);
            var lus = partiel.Lire();
            lus.IsOk.Should().BeTrue("ce qui a été lu sert au point sain et au rapprochement");
            lus.Value.Should().ContainSingle().Which.Code.Should().Be(0x144);
            partiel.Refuses.Should().Be(1);
        }
        finally
        {
            aclDossier.RemoveAccessRule(refus);
            dossier.SetAccessControl(aclDossier);
            aclFichier.RemoveAccessRule(refus);
            wer.SetAccessControl(aclFichier);
        }
    }

    [Fact]
    public void LE_CODE_EST_LU_EN_HEXADECIMAL_SINON_UN_WATCHDOG_GPU_DEVIENT_UN_CODE_INCONNU()
    {
        // Le rapport reel du 22/08/2026 sur la machine de reference.
        Rapport("Kernel_193_abc", "LiveKernelEvent", "193", "80e",
            134318324727177547, "WATCHDOG-20260822-0234.dmp");

        var r = new WerLiveKernelReportLog(_racine).Lire();

        r.IsOk.Should().BeTrue();
        var rapport = r.Value.Should().ContainSingle().Subject;
        rapport.Code.Should().Be(0x193, "lu en decimal, 193 deviendrait 0xC1");
        rapport.NomSdk.Should().Be("VIDEO_DXGKRNL_LIVEDUMP");
        rapport.Bucket.Should().Be("WATCHDOG");
    }

    [Fact]
    public void LES_CHAMPS_SONT_LUS_PAR_INDICE_MEME_QUAND_WINDOWS_LES_A_TRADUITS()
    {
        // `Sig[1].Name` vaut « Paramètre 1 » ici. Le parametre doit sortir quand meme : c'est
        // lui qui distingue deux incidents de meme code.
        Rapport("Kernel_193_def", "LiveKernelEvent", "193", "80e", 134318324727177547);

        new WerLiveKernelReportLog(_racine).Lire()
            .Value.Should().ContainSingle().Which.Parametre1.Should().Be("80e");
    }

    [Fact]
    public void UN_CODE_ABSENT_DE_LA_TABLE_RESTE_UN_CHIFFRE_ET_NE_RECOIT_PAS_DE_NOM_PLAUSIBLE()
    {
        Rapport("Kernel_ABC_xyz", "LiveKernelEvent", "ABC", "1", 134318324727177547);

        var rapport = new WerLiveKernelReportLog(_racine).Lire().Value.Should().ContainSingle().Subject;
        rapport.Code.Should().Be(0xABC);
        rapport.NomSdk.Should().BeNull();
        rapport.Phrase.Should().Contain("non repertorie");
    }

    [Fact]
    public void UN_RAPPORT_QUI_N_EST_PAS_UN_INCIDENT_LIVE_EST_IGNORE()
    {
        // C'est l'en-tete `EventType` qui garantit la position des `Sig[N]`. Sans ce garde-fou,
        // l'indice 0 d'un autre schema serait lu comme un code d'arret.
        Rapport("Kernel_193_ghi", "AppCrash", "193", "80e", 134318324727177547);

        new WerLiveKernelReportLog(_racine).Lire().Value.Should().BeEmpty();
    }

    [Fact]
    public void LES_RAPPORTS_SORTENT_DU_PLUS_RECENT_AU_PLUS_ANCIEN()
    {
        Rapport("Kernel_193_a", "LiveKernelEvent", "193", "80e", 134312243291992623); // 15/08
        Rapport("Kernel_144_b", "LiveKernelEvent", "144", "1009", 134161909376864341); // 22/02
        Rapport("Kernel_193_c", "LiveKernelEvent", "193", "80e", 134318324727177547); // 22/08

        var quand = new WerLiveKernelReportLog(_racine).Lire().Value!.Select(r => r.Quand).ToList();

        quand.Should().HaveCount(3).And.BeInDescendingOrder();
    }

    [Fact]
    public void UN_DOSSIER_WER_ABSENT_REND_UNE_LISTE_VIDE_ET_NON_UNE_ERREUR()
    {
        // Une machine sans aucun rapport est un cas normal, pas une panne de lecture.
        Directory.CreateDirectory(_racine);
        var r = new WerLiveKernelReportLog(Path.Combine(_racine, "inexistant")).Lire();

        r.IsOk.Should().BeTrue();
        r.Value.Should().BeEmpty();
    }

    [Theory]
    // Les cinq codes dont le nom SDK designe le noyau graphique.
    [InlineData(0x113u, true)]  // VIDEO_DXGKRNL_FATAL_ERROR
    [InlineData(0x116u, true)]  // VIDEO_TDR_FAILURE
    [InlineData(0x117u, true)]  // VIDEO_TDR_TIMEOUT_DETECTED
    [InlineData(0x141u, true)]  // VIDEO_ENGINE_TIMEOUT_DETECTED
    [InlineData(0x193u, true)]  // VIDEO_DXGKRNL_LIVEDUMP
    // Le piege : `USBXHCI` et `WATCHDOG` cohabitent dans les memes rapports. Classer sur le
    // bucket ferait passer un gardien USB pour un incident graphique, et gelerait la carte pour
    // une faute qui n'est pas la sienne.
    [InlineData(0x144u, false)] // BUGCODE_USB3_DRIVER
    [InlineData(0x124u, false)] // WHEA_UNCORRECTABLE_ERROR
    // Un code que nous ne savons pas nommer ne devient pas graphique par defaut.
    [InlineData(0xABCu, false)]
    public void SEULS_LES_CODES_DU_NOYAU_GRAPHIQUE_PEUVENT_RETENIR_LA_MAIN_SUR_LA_CARTE(
        uint code, bool graphique)
    {
        new LiveKernelReport(DateTimeOffset.UtcNow, code, null, null, null)
            .EstGraphique.Should().Be(graphique);
    }
}
