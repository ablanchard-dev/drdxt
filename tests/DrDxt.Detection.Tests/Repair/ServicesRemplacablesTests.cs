using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Un service qui tourne en SYSTEM depuis un dossier que le compte peut modifier est une
/// élévation de privilèges classique (vue sur la machine de reference : PresentMonService). L'audit la cherche ; il lui
/// faut d'abord le vrai chemin du binaire, que `PathName` donne avec guillemets, arguments ou variables.
/// </summary>
public sealed class ServicesRemplacablesTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\svc.exe\" --run", "C:\\Program Files\\App\\svc.exe")]
    [InlineData("C:\\Windows\\System32\\svchost.exe -k netsvcs -p", "C:\\Windows\\System32\\svchost.exe")]
    [InlineData("C:\\Outils\\Mon Service.EXE /x", "C:\\Outils\\Mon Service.EXE")]
    [InlineData("\"C:\\Users\\x\\DrDXT\\PresentMonService.exe\"", "C:\\Users\\x\\DrDXT\\PresentMonService.exe")]
    public void Le_chemin_du_binaire_se_lit_malgre_guillemets_et_arguments(string pathName, string attendu)
        => DrDxt.Core.CheminProtege.BinaireDuService(pathName).Should().Be(attendu);

    [Fact]
    public void Les_variables_d_environnement_sont_developpees()
        => DrDxt.Core.CheminProtege.BinaireDuService("%SystemRoot%\\System32\\lsass.exe")
            .Should().Be(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "lsass.exe"));

    private sealed class WmiCompteur : DrDxt.Detection.Wmi.IWmiQuery
    {
        public int RequetesServices { get; private set; }
        public DrDxt.Core.Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> Query(string wql)
        {
            if (wql.Contains("Win32_Service", StringComparison.Ordinal)) RequetesServices++;
            return DrDxt.Core.Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>.Ok(Array.Empty<DrDxt.Detection.Wmi.WmiRow>());
        }
        public DrDxt.Core.Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>> QueryInNamespace(string ns, string wql) => Query(wql);
    }

    [Fact]
    public void Les_deux_familles_de_securite_partagent_une_seule_lecture_des_services()
    {
        // Chaque famille faisait sa propre requête (200 à 340 ms mesurées chacune), à chaque scan.
        var wmi = new WmiCompteur();
        new RepairAuditService(new ServicesMuets(), new NoyauMuet(), wmi: wmi).Lire();
        wmi.RequetesServices.Should().Be(1);
    }

    [Theory]
    [InlineData(@"C:\A\x.exe appartient à un compte", "ce fichier appartient à un compte")]
    [InlineData(@"C:\A est modifiable par Utilisateurs", @"son dossier (C:\A) est modifiable par Utilisateurs")]
    [InlineData("ACL illisible (X)", "ACL illisible (X)")]
    public void Le_chemin_deja_affiche_en_valeur_n_est_pas_repete_dans_l_explication(string raison, string attendu)
        => RepairAuditService.SansLeChemin(raison, @"C:\A\x.exe").Should().Be(attendu);

    // Même règle que les tâches. Un service désactivé ne démarre pas : le constat ne dit pas
    // « il s'exécuterait au prochain démarrage du service » sans dire d'abord qu'il est désactivé.
    [Theory]
    [InlineData("Disabled", true)]
    [InlineData("Manual", false)]
    public void Un_service_desactive_le_dit(string demarrage, bool desactive)
    {
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var exe = Path.Combine(dossier, "svc.exe");
            File.WriteAllBytes(exe, [0]);
            var ligne = new DrDxt.Detection.Wmi.WmiRow(new Dictionary<string, object?>
            {
                ["Name"] = "Svc", ["DisplayName"] = "Mon service", ["PathName"] = $"\"{exe}\"",
                ["StartName"] = "LocalSystem", ["StartMode"] = demarrage,
            });
            var c = RepairAuditService.ServicesRemplacables(
                DrDxt.Core.Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>.Ok(new[] { ligne })).Single();
            var e = c.Explication;
            if (desactive) e.Should().Contain("désactivé").And.Contain("réactivé");
            else e.Should().StartWith("ce service tourne avec les droits du système");
            // Le geste se lit à part, en étapes ; l'explication garde le quoi et le pourquoi.
            c.Remede.Should().StartWith("1) ").And.Contain("Program Files");
            e.Should().NotContain("Réinstaller").And.EndWith("DrDXT ne change ses droits que si tu appuies sur « Corriger les droits ».");
            // Le geste vise la clé du service, sous son nom de service : c'est là que le helper relit le chemin.
            c.Cible.Should().Be("Svc");
        }
        finally { Directory.Delete(dossier, recursive: true); }
    }

    // Même règle pour le chemin non cité : un service désactivé ne démarre pas.
    [Theory]
    [InlineData("Disabled", true)]
    [InlineData("Auto", false)]
    public void Un_chemin_non_cite_d_un_service_desactive_le_dit(string demarrage, bool desactive)
    {
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-nc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            // « …\Mon Service.exe » sans guillemets : Windows essaie d'abord « …\Mon.exe », posable ici.
            var ligne = new DrDxt.Detection.Wmi.WmiRow(new Dictionary<string, object?>
            {
                ["Name"] = "Svc", ["DisplayName"] = "Svc", ["PathName"] = Path.Combine(dossier, "Mon Service.exe"),
                ["StartName"] = "LocalSystem", ["StartMode"] = demarrage,
            });
            var c = RepairAuditService.CheminsNonCites(
                DrDxt.Core.Result<IReadOnlyList<DrDxt.Detection.Wmi.WmiRow>>.Ok(new[] { ligne })).Single();
            var e = c.Explication;
            if (desactive) e.Should().Contain("désactivé").And.Contain("réactivé");
            else e.Should().NotContain("désactivé");
            // La clé du service lu, pas une clé générique : c'est là que vit le chemin à mettre entre guillemets.
            c.Remede.Should().StartWith("1) ").And.Contain(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Svc").And.Contain("ImagePath");
            e.Should().NotContain("Mettre le chemin").And.EndWith("DrDXT ne touche pas à ce service.");
            c.Cible.Should().BeNull("aucun geste ne corrige un chemin non cité");
        }
        finally { Directory.Delete(dossier, recursive: true); }
    }

    // Pour un service déjà sous C:\Program Files (Epic Games Updater), « Réinstaller le logiciel dans Program Files »
    // n'a pas de sens. Le remède dépend de l'endroit où vit le fichier.
    [Fact]
    public void Le_remede_ne_conseille_pas_Program_Files_a_un_fichier_qui_y_est_deja()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dedans = RepairAuditService.Remede(Path.Combine(pf, "Epic Games", "EpicGamesUpdater.exe"), "Réinstaller dans Program Files.");
        dedans.Should().NotContain("Réinstaller dans Program Files").And.Contain("déjà sous Program Files");

        RepairAuditService.Remede(@"C:\Users\x\DrDXT\PresentMonService.exe", "Réinstaller dans Program Files.")
            .Should().Be("Réinstaller dans Program Files.");
        RepairAuditService.Remede(pf + @" Piege\x.exe", "Réinstaller dans Program Files.")
            .Should().Be("Réinstaller dans Program Files.", "un dossier qui commence par le même nom n'est pas Program Files");
    }

    // Le service d'un jeu vit sous ProgramData, dossier choisi par l'éditeur ; « réinstaller dans Program
    // Files » est impossible au joueur. Le remède dit qui peut fermer ce chemin, et le risque de le fermer soi-même.
    [Fact]
    public void Sous_ProgramData_le_remede_ne_conseille_pas_une_reinstallation_impossible()
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var remede = RepairAuditService.Remede(Path.Combine(pd, "Editeur", "Jeu", "Service.exe"), "Réinstaller dans Program Files.");

        remede.Should().NotContain("Réinstaller dans Program Files")
            .And.Contain("choisi par l'éditeur").And.Contain("1) Signale-le-lui");
    }

    // « retirer ce droit d'écriture » dit comment. Sous Program Files, le fichier reprend les droits de son
    // dossier une fois rendu aux Administrateurs : la même commande que l'installeur de DrDXT, vérifiée sur ses
    // 693 fichiers, et le dossier d'Epic lu, hérité de Program Files.
    [Fact]
    public void Sous_Program_Files_le_remede_donne_la_commande_exacte_sur_le_fichier_nomme()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "EpicGamesUpdater.exe");

        var remede = RepairAuditService.Remede(exe, "Réinstaller dans Program Files.");

        remede.Should().Contain($"icacls \"{exe}\" /setowner *S-1-5-32-544")
            .And.Contain($"icacls \"{exe}\" /reset");
        remede.IndexOf("/setowner", StringComparison.Ordinal).Should()
            .BeLessThan(remede.IndexOf("/reset", StringComparison.Ordinal), "le recalcul lit le propriétaire");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\\SystemRoot\\System32\\drivers\\pilote.sys")]
    public void Sans_executable_reconnaissable_rien_n_est_rendu(string? pathName)
        => DrDxt.Core.CheminProtege.BinaireDuService(pathName).Should().BeNull();
}
