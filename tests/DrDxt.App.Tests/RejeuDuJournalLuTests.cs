using System.IO;
using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Le rapport du rejeu, dit une fois au joueur : un rapport plus récent que le dernier dit, et ses seules lignes
/// jugées.</summary>
public sealed class RejeuDuJournalLuTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-rejeu-lu-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private static readonly DateTimeOffset Le = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static RapportDuRejeu Rapport(DateTimeOffset le, params RapportDuRejeu.Ligne[] lignes) => new(le, null, lignes);
    private static readonly RapportDuRejeu.Ligne Rendue = new("registre HwSchMode", IssueDuRendu.Rendu, "remise à 1", false);
    private static readonly RapportDuRejeu.Ligne Respectee = new("plan actif", IssueDuRendu.Respecte, "changé depuis", false);
    private static readonly RapportDuRejeu.Ligne EnSuspens = new("HKCU", IssueDuRendu.Illisible, "session", true);

    private RejeuDuJournalLu Lu(params RapportDuRejeu[] rapports) => new(() => rapports, Path.Combine(_dossier, "dit.txt"));

    // Le rapport dit sans pouvoir être noté comme dit reviendra au lancement suivant : il le dit, au lieu de se taire.
    [Fact]
    public void Un_rapport_qui_n_a_pas_pu_etre_retenu_le_dit()
    {
        var fichierALaPlaceDuDossier = Path.Combine(_dossier, "pas-un-dossier");
        File.WriteAllText(fichierALaPlaceDuDossier, "");
        var lu = new RejeuDuJournalLu(() => [Rapport(Le, Rendue)], Path.Combine(fichierALaPlaceDuDossier, "dit.txt"));

        lu.TexteNouveau().Should().Contain("registre HwSchMode").And.EndWith(Docteur.T("JEA.NonRetenu"));
    }

    [Fact]
    public void Un_rapport_nouveau_se_dit_une_fois()
    {
        var rapport = Rapport(Le, Rendue, Respectee, EnSuspens);

        var texte = Lu(rapport).TexteNouveau();

        texte.Should().StartWith(Docteur.T("JEA.Rapport", 1, 1, 0)).And.Contain("registre HwSchMode : remise à 1").And.NotContain("HKCU");
        Lu(rapport).TexteNouveau().Should().BeNull("déjà dit");
        Lu(rapport, Rapport(Le.AddMinutes(1), Rendue)).TexteNouveau().Should().NotBeNull("plus récent");
    }

    // Une horloge partie en avant puis corrigée : le rapport écrit ensuite est daté AVANT le dernier dit. Retenir une date
    // maximale le cachait pour toujours ; retenir chaque rapport dit le laisse se dire.
    [Fact]
    public void Un_rapport_jamais_dit_se_dit_meme_date_avant_le_dernier_dit()
    {
        var enAvance = Rapport(Le.AddDays(30), Rendue);
        Lu(enAvance).TexteNouveau().Should().NotBeNull();

        Lu(enAvance, Rapport(Le, Respectee)).TexteNouveau().Should().NotBeNull("écrit après la correction de l'horloge, jamais dit");
        Lu(enAvance, Rapport(Le, Respectee)).TexteNouveau().Should().BeNull("dit une fois");
    }

    // Le fichier d'avant ne portait qu'une date : tout rapport jusqu'à elle a déjà été dit, rien ne se redit après la mise à jour.
    [Fact]
    public void L_ancien_fichier_a_une_date_ne_fait_rien_redire()
    {
        File.WriteAllText(Path.Combine(_dossier, "dit.txt"), Le.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        Lu(Rapport(Le, Rendue), Rapport(Le.AddMinutes(-5), Rendue)).TexteNouveau().Should().BeNull();
        Lu(Rapport(Le, Rendue), Rapport(Le.AddMinutes(5), Respectee)).TexteNouveau().Should().Contain("plan actif");
    }

    [Fact]
    public void Sans_rapport_ou_tout_en_suspens_rien_ne_se_dit()
    {
        Lu().TexteNouveau().Should().BeNull();
        Lu(Rapport(Le, EnSuspens)).TexteNouveau().Should().BeNull();
        Lu(Rapport(Le.AddMinutes(1), Rendue)).TexteNouveau().Should().NotBeNull("un rapport en suspens ne compte pas comme dit");
    }

    // Au lancement, après le constat de l'App sur son propre essai : le rapport s'ajoute au message de démarrage.
    [Fact]
    public async Task Au_lancement_le_rapport_s_ajoute_au_message_de_demarrage()
    {
        var vm = ScanViewModelTestSeams.Build(NSubstitute.Substitute.For<IHardwareDetector>(), NSubstitute.Substitute.For<ISettingsStateReader>(),
            NSubstitute.Substitute.For<IBenchmarkRunner>(), NSubstitute.Substitute.For<IScanScorer>(), NSubstitute.Substitute.For<IStorageDetector>(), k => k);
        vm.RejeuLu = Lu(Rapport(Le, Rendue));
        var journal = NSubstitute.Substitute.For<DrDxt.Application.Autopilot.IRevertEnAttente>();
        NSubstitute.SubstituteExtensions.Returns(journal.Lire(), Result<DrDxt.Application.Autopilot.RevertEnAttenteEntree?>.Ok(null));

        await vm.RendreSessionInterrompueAsync(journal, NSubstitute.Substitute.For<ITweakCatalog>());

        vm.MessageDemarrage.Should().StartWith(Docteur.T("JEA.Rapport", 1, 0, 0));
    }

    // L'App tuée, puis Windows redémarre : le service rend la ruche de la machine, le helper celle du joueur à la connexion de
    // l'App. Les deux rapports se disent, aucun n'a effacé l'autre.
    [Fact]
    public void Les_rapports_du_service_et_du_helper_se_disent_ensemble()
    {
        var racine = Path.Combine(_dossier, "racine");
        Directory.CreateDirectory(racine);
        Result Ecrire(string chemin, byte[] octets) { File.WriteAllBytes(chemin, octets); return Result.Ok(); }
        RapportDuRejeu.Ecrire(racine, RapportDuRejeu.Service, new RapportDuRendu(null,
            [new LigneDuRendu(RejeuDuJournal.Source, "registre HKLM", IssueDuRendu.Rendu, "machine rendue")]), Le, Ecrire, _ => true).IsOk.Should().BeTrue();
        RapportDuRejeu.Ecrire(racine, RapportDuRejeu.Helper, new RapportDuRendu(null,
            [new LigneDuRendu(RejeuDuJournal.Source, "registre HKCU", IssueDuRendu.Rendu, "session rendue")]), Le.AddMinutes(5), Ecrire, _ => true).IsOk.Should().BeTrue();
        RapportDuRejeu.Ecrire(racine, RapportDuRejeu.Helper, new RapportDuRendu(null,
            [new LigneDuRendu(RejeuDuJournal.Source, "plan actif", IssueDuRendu.Rendu, "plan rendu")]), Le.AddMinutes(6), Ecrire, _ => true).IsOk.Should().BeTrue();

        var texte = new RejeuDuJournalLu(() => RapportDuRejeu.Lire(racine, _ => true), Path.Combine(_dossier, "dit.txt")).TexteNouveau();

        texte.Should().StartWith(Docteur.T("JEA.Rapport", 3, 0, 0)).And.Contain("machine rendue").And.Contain("session rendue").And.Contain("plan rendu");
    }
}
