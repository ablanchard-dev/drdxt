using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La porte du point de restauration, et ce qu'Optimiser dit de son résultat.</summary>
public sealed partial class ScanApplyCommandTests
{
    // ====================== porte du point de restauration ======================

    [Fact]
    public async Task Restore_point_gate_runs_before_any_apply()
    {
        var h = Build();
        await ScanThenApply(h);

        Received.InOrder(() =>
        {
            h.Safety.EnsureRestorePointAsync();
            h.Engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Restore_disabled_blocks_apply_and_shows_localized_message()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.power_plan" }, pid: PidDuJeu,
            restoreResult: Result.Fail(ErrorCodes.RestoreDisabled, "off"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.CandidatsCrees.Should().BeEmpty("rien ne s'ecrit sans point de restauration");
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Blocked");
    }

    [Fact]
    public async Task Restore_declined_blocks_apply_with_message()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreDeclined, "declined"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Declined");
    }

    // La restauration peut être active et Windows échouer à créer le point : l'écran ne doit pas dire « la
    // restauration est désactivée ». Chaque cause a ses mots, et la raison de Windows est dite.
    [Fact]
    public async Task Un_point_que_Windows_n_a_pas_cree_ne_se_dit_pas_restauration_desactivee()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreFailed, "aucun point neuf"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Failed", "pas « Blocked », qui dit la restauration désactivée");
    }

    [Fact]
    public async Task Un_point_accepte_mais_introuvable_le_dit_sans_accuser_Windows()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreNotVerified, "rien en 21 s"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.NotFound");
    }

    // Chez moi : « je clique et rien ». « Optimiser » avait posé le VRR, mais son résultat (et ses refus)
    // s'écrivaient ~900 lignes plus bas, sous le pli. Le résultat suit le bouton, avant le choix de scène.
    [Fact]
    public void Le_resultat_d_Optimiser_s_affiche_sous_le_bouton()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var bouton = xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal);
        var scene = xaml.IndexOf("{Binding LabelSceneRetenue}", StringComparison.Ordinal);
        bouton.Should().BePositive("témoin");
        scene.Should().BeGreaterThan(bouton, "témoin");
        foreach (var message in new[] { "ResultatOptimisation", "ApplyBlockedMessage", "MoatUnarmedMessage", "ManifesteIllisibleMessage" })
            xaml.IndexOf($"{{Binding {message}}}", StringComparison.Ordinal).Should()
                .BeInRange(bouton, scene, $"{message} doit se lire sous le bouton qui le produit");
    }

    // Jusqu'à ~40 s sans retour (création du point puis relectures) ; le bouton le dit.
    [Fact]
    public void Optimiser_dit_qu_il_travaille_pendant_le_point_de_restauration()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var apres = xaml[xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal)..];
        apres.Should().Contain("{Binding LabelOptimisationEnCours}");
        apres[..apres.IndexOf("LabelOptimisationEnCours", StringComparison.Ordinal)]
            .Should().Contain("ApplyCommand.IsRunning", "le texte n'apparaît que pendant le travail");
    }

    // Un essai en alternance tient son levier : Optimiser ne le mesure pas en A/B par-dessus, sinon les parties suivantes
    // seraient marquées d'un état que la session a changé.
    [Fact]
    public async Task Optimiser_ne_mesure_pas_le_levier_d_un_essai_en_cours()
    {
        var h = Build(graines: new[] { "latency.reflex" }, pid: PidDuJeu);
        var dossier = Directory.CreateTempSubdirectory("drdxt-essai-opt-").FullName;
        try
        {
            h.Vm.MemoireEssai = new DrDxt.Application.Parties.MemoireDeLEssai(Path.Combine(dossier, "essai.json"));
            h.Vm.MemoireEssai.Ecrire(new DrDxt.Application.Parties.EssaiEnAlternance("latency.reflex", false, false, DateTimeOffset.Now))
                .IsOk.Should().BeTrue();

            await ScanThenApply(h);

            h.CandidatsCrees.Should().NotContain("latency.reflex");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Le résultat d'un Optimiser précédent ne reste pas sous le bouton après un nouveau scan : il contredirait la nouvelle liste.
    [Fact]
    public async Task Un_nouveau_scan_efface_le_resultat_d_Optimiser()
    {
        var h = Build(graines: new[] { "fixit.heures_actives" });
        await ScanThenApply(h);
        h.Vm.ResultatOptimisation.Should().NotBeNullOrEmpty("témoin : Optimiser a parlé");

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.ResultatOptimisation.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Engine_failure_surfaces_error()
    {
        var h = Build(applyResult: Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "boom"));
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.Failed");
    }

    // La réponse du helper manque après l'envoi : le réglage a pu être posé. « Rien n'a été posé » serait faux ; seule la
    // phrase de l'incertitude se dit.
    [Fact]
    public async Task Sans_reponse_du_helper_Optimiser_ne_dit_pas_que_rien_n_a_ete_pose()
    {
        var h = Build(applyResult: Result<ApplyReport>.Fail(ErrorCodes.HelperNoReply, "pas de réponse"));
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Erreur.Generique", "la phrase de l'incertitude, pas « Apply.Failed »");
    }
}
