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
    private static ViewModels.ScanViewModel Vm(bool eleve = false)
        => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k + " {0}",
            estEleve: eleve);

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

    [Fact]
    public void Une_version_verifiee_est_nommee_et_attend_le_clic()
    {
        var vm = Vm();

        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(@"C:\staging\payload.pkg"))));

        vm.VersionProposee.Should().Be("0.9.1");
        vm.LabelFooterUpdates.Should().Be("UI.Footer.Updates.NewVersionNamed 0.9.1");
        vm.CanInstallUpdate.Should().BeTrue();
    }

    // L'installeur vérifié n'est plus là (ou a changé) ⇒ refusé ; la version n'est plus proposée.
    [Fact]
    public async Task Un_installeur_refuse_retire_la_proposition()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(Proposee(_ => Task.FromResult(Result<string>.Ok(
            Path.Combine(Path.GetTempPath(), "drdxt-absent-" + Guid.NewGuid().ToString("N"), "payload.pkg")))));

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.InstallerRefused");
        vm.VersionProposee.Should().BeNull();
        vm.CanInstallUpdate.Should().BeFalse();
        vm.InstallUpdateCommand.CanExecute(null).Should().BeFalse();
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
