using System.IO;
using DrDxt.App.Updates;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Une version signée et vérifiée est nommée (« une nouvelle version » ne dirait pas laquelle) et exposée au
/// contrat de l'interface ; rien ne s'installe sans le clic.
/// </summary>
public class MiseAJourProposeeTests
{
    // Le jeton est fixé : ces tests tournent aussi en administrateur (CI, poste de dev), où une App élevée refuse d'installer.
    // L'UAC est fixé aussi : un poste de CI peut l'avoir coupé.
    private static ViewModels.ScanViewModel Vm(bool eleve = false)
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k + " {0}",
            estEleve: eleve);
        vm.UacCoupe = () => false;
        return vm;
    }

    // Depuis une App relancée en administrateur, rien ne se télécharge ni ne s'écrit dans le dossier du compte (une jonction
    // posée d'avance y redirigerait l'écriture), et le setup n'hériterait jamais du jeton élevé. La version reste proposée :
    // il suffit de relancer l'App normalement.
    [Fact]
    public async Task Une_App_elevee_ne_telecharge_rien_et_dit_de_relancer_normalement()
    {
        var vm = Vm(eleve: true);
        var appels = 0;
        vm.AfficherMiseAJour(Proposee(_ => { appels++; return Task.FromResult(Result<string>.Ok(@"C:\staging\payload.pkg")); }));

        vm.CanInstallUpdate.Should().BeFalse();
        vm.LabelFooterUpdates.Should().Be("UI.Footer.Updates.NewVersionNamed 0.9.1 · " + Docteur.T("MAJ.InstalleurEleve"));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        appels.Should().Be(0, "aucun téléchargement depuis un processus élevé");
        vm.LabelFooterUpdates.Should().EndWith(Docteur.T("MAJ.InstalleurEleve"));
        vm.VersionProposee.Should().Be("0.9.1", "la version reste proposée");
    }

    // L'installeur ferme l'App et le helper d'office : un réglage temporaire posé (palier de sous-tension, décalage ou
    // ventilateur Expert) ne serait plus rendu. Rien ne se télécharge ni ne s'installe tant qu'il reste posé, et le pied
    // de page dit quoi faire ; la version reste proposée.
    [Fact]
    public async Task Rien_ne_s_installe_tant_qu_un_reglage_temporaire_reste_a_rendre()
    {
        var vm = Vm();
        var appels = 0;
        vm.AfficherMiseAJour(Proposee(_ => { appels++; return Task.FromResult(Result<string>.Ok(@"C:\staging\payload.pkg")); }));
        vm.RechercheSousTensionEnCours = true;

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        appels.Should().Be(0);
        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.ARendreDAbord");
        vm.VersionProposee.Should().Be("0.9.1");
    }

    // Le réglage temporaire posé pendant le téléchargement : l'installeur ne part pas non plus.
    [Fact]
    public async Task Un_reglage_pose_pendant_le_telechargement_retient_l_installeur()
    {
        var vm = Vm();
        var dossier = Directory.CreateTempSubdirectory("drdxt-arendre-").FullName;
        try
        {
            var paquet = Path.Combine(dossier, "payload.pkg");
            File.WriteAllText(paquet, "paquet");
            vm.AfficherMiseAJour(Proposee(_ => { vm.RechercheSousTensionEnCours = true; return Task.FromResult(Result<string>.Ok(paquet)); }));

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.ARendreDAbord");
            vm.VersionProposee.Should().Be("0.9.1");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // UAC coupé : chaque processus du compte tourne déjà avec le jeton administrateur, il n'y a pas de « relancer
    // normalement ». Le refus protégeait une frontière qui n'existe plus : l'installation part, et rien ne le dit.
    [Fact]
    public async Task Avec_l_UAC_coupe_l_App_elevee_installe()
    {
        var vm = Vm(eleve: true);
        vm.UacCoupe = () => true;
        var appels = 0;
        vm.AfficherMiseAJour(Proposee(_ => { appels++; return Task.FromResult(Result<string>.Fail("x", "arrêt du test")); }));

        vm.CanInstallUpdate.Should().BeTrue();
        vm.LabelFooterUpdates.Should().NotContain(Docteur.T("MAJ.InstalleurEleve"));
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        appels.Should().Be(1, "le téléchargement part");
    }

    // L'installeur ferme l'App et le helper d'office : lancé pendant « Vérifier DrDXT sur mon PC » ou Optimiser, il laissait un
    // levier de test posé. Pendant une opération longue, « Installer » se grise, et se rallume à sa fin.
    [Fact]
    public void Installer_attend_la_fin_d_une_operation_longue()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(@"C:\staging\payload.pkg"))));

        vm.VerificationEnCours = true;
        vm.CanInstallUpdate.Should().BeFalse();
        vm.InstallUpdateCommand.CanExecute(null).Should().BeFalse();

        vm.VerificationEnCours = false;
        vm.CanInstallUpdate.Should().BeTrue();
    }

    [Fact]
    public void Une_version_verifiee_est_nommee_et_attend_le_clic()
    {
        var vm = Vm();

        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(@"C:\staging\payload.pkg"))));

        vm.VersionProposee.Should().Be("0.9.1");
        vm.LabelFooterUpdates.Should().Be("UI.Footer.Updates.NewVersionNamed 0.9.1");
        vm.CanInstallUpdate.Should().BeTrue();
    }

    // L'installeur a changé depuis la vérification (empreinte différente) ⇒ refusé ; la version n'est plus proposée.
    [Fact]
    public async Task Un_installeur_change_retire_la_proposition()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-change-").FullName;
        try
        {
            var paquet = Path.Combine(dossier, "payload.pkg");
            File.WriteAllText(paquet, "pas le paquet vérifié");
            var vm = Vm();
            vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(paquet))));

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.InstallerRefused");
            vm.VersionProposee.Should().BeNull();
            vm.CanInstallUpdate.Should().BeFalse();
            vm.InstallUpdateCommand.CanExecute(null).Should().BeFalse();
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Un installeur qui ne part pas (fichier introuvable, autorisation Windows refusée, disque) n'a pas « changé » : la
    // version reste proposée, un nouveau clic réessaie.
    [Fact]
    public async Task Un_installeur_qui_ne_part_pas_garde_la_proposition()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(
            Path.Combine(Path.GetTempPath(), "drdxt-absent-" + Guid.NewGuid().ToString("N"), "payload.pkg")))));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.InstallerNotLaunched");
        vm.VersionProposee.Should().Be("0.9.1");
        vm.CanInstallUpdate.Should().BeTrue();
    }

    [Fact]
    public void Seule_une_empreinte_changee_est_un_refus()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-lance-").FullName;
        try
        {
            var paquet = Path.Combine(dossier, "payload.pkg");
            File.WriteAllText(paquet, "paquet");
            var empreinte = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(paquet))).ToLowerInvariant();

            InstallationMiseAJour.Lancer(paquet, empreinte, "0.9.1", _ => false, () => false).Error!.Value.Code
                .Should().Be(InstallationMiseAJour.CodeNonLance, "autorisation Windows refusée : rien n'a changé");
            InstallationMiseAJour.Lancer(paquet, "00", "0.9.1", _ => true, () => false).Error!.Value.Code
                .Should().Be(InstallationMiseAJour.CodeRefus);
        }
        finally { Directory.Delete(dossier, true); }
    }

    private static ResultatMiseAJour Proposee(Func<CancellationToken, Task<Result<string>>> telecharger)
        => new(EtatMiseAJour.NouvelleVersionVerifiee, "ab12", "0.9.1", telecharger);

    // L'installeur ne part qu'au clic ; pendant son téléchargement, le bouton se tait (un second clic doublerait tout).
    [Fact]
    public async Task Le_telechargement_part_au_clic_et_le_bouton_se_tait_pendant()
    {
        var vm = Vm();
        var appels = 0;
        var fin = new TaskCompletionSource<Result<string>>();
        vm.AfficherMiseAJour(Proposee(_ => { appels++; return fin.Task; }));
        appels.Should().Be(0, "rien ne se télécharge sans le clic");

        var clic = vm.InstallUpdateCommand.ExecuteAsync(null);
        await Task.Delay(50);
        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.Downloading");
        vm.CanInstallUpdate.Should().BeFalse();

        fin.SetResult(Result<string>.Fail(DrDxt.Infrastructure.Updates.UpdateErrorCodes.Offline, "coupé"));
        await clic;
        appels.Should().Be(1);
        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.Offline");
        vm.CanInstallUpdate.Should().BeTrue("un réseau coupé laisse réessayer");
    }

    // Une panne imprévue au clic (le client libéré levait ObjectDisposedException) remontait au fil de l'écran : elle se dit
    // au pied de page, et la version reste proposée.
    [Fact]
    public async Task Une_panne_imprevue_au_telechargement_se_dit_sans_planter()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(Proposee(_ => throw new ObjectDisposedException("System.Net.Http.HttpClient")));

        await vm.Invoking(v => v.InstallUpdateCommand.ExecuteAsync(null)).Should().NotThrowAsync();

        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.Failed");
        vm.CanInstallUpdate.Should().BeTrue("un nouveau clic réessaie");
    }

    [Fact]
    public async Task Un_paquet_refuse_au_telechargement_n_est_plus_propose()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(
            Result<string>.Fail(DrDxt.Infrastructure.Updates.UpdateErrorCodes.HashMismatch, "empreinte"))));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.Rejected");
        vm.VersionProposee.Should().BeNull();
        vm.CanInstallUpdate.Should().BeFalse();
    }

    // Les tables reçues par un paquet signé sont dites au pied de page, à côté de l'état de la mise à jour.
    [Fact]
    public void Les_tables_recues_et_un_paquet_refuse_se_disent_au_pied_de_page()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(new ResultatMiseAJour(EtatMiseAJour.AJour));

        vm.AfficherDonnees(TablesDeDonnees.Embarquees, refuse: false);
        vm.LabelFooterUpdates.Should().Be("UI.Footer.Updates.UpToDate {0}", "les tables livrées avec l'App ne s'annoncent pas");

        vm.AfficherDonnees(TablesDeDonnees.Embarquees with { Version = 3, Publie = new DateOnly(2026, 10, 17) }, refuse: false);
        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.UpToDate {0} · UI.Footer.Data 3");

        vm.AfficherDonnees(TablesDeDonnees.Embarquees, refuse: true);
        vm.LabelFooterUpdates.Should().EndWith(" · UI.Footer.Data.Rejected {0}");
    }

    [Theory]
    [InlineData(EtatMiseAJour.NonConfigure)]
    [InlineData(EtatMiseAJour.Refusee)]
    [InlineData(EtatMiseAJour.AJour)]
    public void Sans_version_verifiee_rien_n_est_propose(EtatMiseAJour etat)
    {
        var vm = Vm();

        vm.AfficherMiseAJour(new ResultatMiseAJour(etat));

        vm.VersionProposee.Should().BeNull();
        vm.CanInstallUpdate.Should().BeFalse();
    }
}
