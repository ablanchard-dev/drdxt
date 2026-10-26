using System.IO;
using DrDxt.Tests.Shared;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La liste signée (ce que le service repose après une mise à jour de Windows) sort avant le journal des leviers (ce que l'écran
// montre) : une App arrêtée entre les deux laisserait un levier caché et encore reposé. Et un levier resté dans la liste seule se
// voit au scan, avec son « Annuler ».
public sealed class ListeSigneeAvantLeJournalTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-liste-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private const string Id = "latency.vrrFlipModelOn";
    private readonly IDesiredStateStore _liste = Substitute.For<IDesiredStateStore>();
    private readonly ITweak _levier = Substitute.For<ITweak>();

    private (ScanViewModel Vm, JournalDesLeviersAppliques Journal, List<string> Sortis) Vm(DesiredState? liste = null)
    {
        var journal = new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json"));
        _liste.LoadAsync().Returns(Result<DesiredState>.Ok(liste ?? new DesiredState([])));
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: journal, manifeste: _liste);
        vm.LevierParId = id => id == Id ? _levier : null;
        var sortis = new List<string>();
        vm.RetirerDuManifeste = id =>
        {
            // Au moment où la liste signée sort, l'écran le montre encore.
            sortis.Add(journal.Lire().Any(s => s.TweakId == id) ? $"{id} (encore au journal)" : id);
            return Task.FromResult(Result.Ok());
        };
        return (vm, journal, sortis);
    }

    [Fact]
    public async Task Annuler_sort_de_la_liste_signee_avant_le_journal()
    {
        var (vm, journal, sortis) = Vm();
        journal.Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false,
            new TweakSnapshot(Id, InversesDeTest.Pour(Id, "origine"), "posee"), null)], null));

        journal.Lire().Should().HaveCount(1, "témoin : le levier est au journal");
        await vm.AnnulerLevierCommand.ExecuteAsync(Id);

        sortis.Should().Equal($"{Id} (encore au journal)");
        journal.Lire().Should().BeEmpty();
    }

    // Le service repose le levier entre le rendu et sa sortie de la liste signée (sa passe d'après une mise à jour de Windows
    // ou du pilote) : l'écran dirait « Annulé » d'un réglage reposé. Relu après la sortie, il est rendu de nouveau, et
    // « Annulé » ne se dit que relu à l'origine.
    [Fact]
    public async Task Un_levier_repose_par_le_service_pendant_l_annulation_est_rendu_de_nouveau()
    {
        var (vm, journal, _) = Vm();
        journal.Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false,
            new TweakSnapshot(Id, InversesDeTest.Pour(Id, "origine"), "posee"), null)], null));
        var pose = EtatApplique.Applied;
        vm.VerifierLaPose = _ => pose;
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { pose = EtatApplique.NotApplied; return Result.Ok(); });
        vm.RetirerDuManifeste = _ => { pose = EtatApplique.Applied; return Task.FromResult(Result.Ok()); };

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);

        pose.Should().Be(EtatApplique.NotApplied, "le réglage reposé pendant l'annulation est rendu de nouveau");
        vm.MessageLeviers.Should().Be("Leviers.Annule");
    }

    [Fact]
    public async Task Un_levier_reste_dans_la_liste_seule_se_voit_et_s_annule_avec_son_origine()
    {
        var origine = InversesDeTest.Pour(Id, "origine");
        var (vm, _, sortis) = Vm(new DesiredState([], [new AppliedTweak(Id, "posee", origine, DateTimeOffset.UtcNow)]));

        (await vm.RelireOrphelinsDuManifesteAsync()).Should().ContainSingle();
        vm.LeviersPoses.Should().ContainSingle().Which.Titre.Should().Be("Leviers.ListeSigneeSeulement");

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);

        await _levier.Received(1).RevertAsync(new TweakSnapshot(Id, origine, "posee"));
        sortis.Should().Equal(Id);
        vm.LeviersPoses.Should().BeEmpty();
    }

    // Une entrée que le service réaffirme, dont l'origine n'est pas une opération que l'App sache rendre : elle sort de la liste,
    // rien ne s'écrit.
    [Fact]
    public async Task Une_entree_du_service_seule_sort_de_la_liste_sans_rien_ecrire()
    {
        var (vm, _, sortis) = Vm(new DesiredState([new AppliedTweak(Id, "1", "0", DateTimeOffset.UtcNow)]));
        await vm.RelireOrphelinsDuManifesteAsync();

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);

        await _levier.DidNotReceiveWithAnyArgs().RevertAsync(default!);
        sortis.Should().Equal(Id);
        vm.MessageLeviers.Should().Be("Leviers.SortiDeLaListe");
    }

    // Optimiser interrompu entre la liste signée et le journal : le levier n'est que dans la liste, en entrée que le service
    // réaffirme. Son origine est celle du levier (le pont la copie du snapshot) ; l'en sortir sans la rendre la perdait du seul
    // endroit qui la portait, et ni Annuler ni la désinstallation ne pouvaient plus rendre le réglage.
    [Fact]
    public async Task Une_entree_du_service_seule_s_annule_avec_l_origine_du_levier()
    {
        const string hags = "latency.hagsOn";
        var origine = InversesDeTest.Pour(hags, "1");
        var posee = InversesDeTest.Pour(hags, "2");
        var (vm, _, sortis) = Vm(new DesiredState([new AppliedTweak(AppliedTweakManifestMapper.IdDuService(hags)!, "2", origine,
            DateTimeOffset.UtcNow, posee)]));
        vm.LevierParId = id => id == hags ? _levier : null;
        await vm.RelireOrphelinsDuManifesteAsync();

        await vm.AnnulerLevierCommand.ExecuteAsync(hags);

        await _levier.Received(1).RevertAsync(new TweakSnapshot(hags, origine, posee));
        sortis.Should().Equal(hags);
    }

    [Fact]
    public async Task Le_retrait_apres_annulation_sort_de_la_liste_avant_le_journal()
    {
        var journal = new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json"));
        journal.Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false,
            new TweakSnapshot(Id, InversesDeTest.Pour(Id, "origine"), "posee"), null)], null));
        var auJournal = new List<bool>();
        _liste.RetirerAsync(Arg.Any<IReadOnlyList<string>>()).Returns(_ =>
        {
            auJournal.Add(journal.Lire().Any(s => s.TweakId == Id));
            return Task.FromResult(Result.Ok());
        });

        (await RetraitDeLevier.ApresAnnulationAsync(_liste, journal, Id)).IsOk.Should().BeTrue();

        auJournal.Should().Equal(true);
        journal.Lire().Should().BeEmpty();
    }

    // Un rejeu (helper ou service) a rendu un levier dont l'App a déjà noté l'origine : au lancement, il n'est plus gardé.
    [Fact]
    public async Task Au_lancement_un_levier_rendu_par_le_rejeu_n_est_plus_garde()
    {
        var (vm, journal, sortis) = Vm();
        var pose = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"Software\Test", "Valeur", RegistryValueType.DWord, "1");
        journal.Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false,
            new TweakSnapshot(Id, InversesDeTest.Pour(Id, "origine"), DrDxt.Core.PrivilegedOpCodec.Serialize(pose).Value!), null)], null));
        journal.Lire().Should().HaveCount(1, "témoin : le levier est gardé");
        vm.VerifierLaPose = _ => EtatApplique.NotApplied;
        string sid;
        using (var moi = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = moi.User!.Value;
        vm.RapportsDuRejeu = () => [new DrDxt.ElevatedHelper.Journal.RapportDuRejeu(DateTimeOffset.UtcNow, null,
            [new("Valeur", DrDxt.Reconciler.Desinstallation.IssueDuRendu.Rendu, "", false, DrDxt.ElevatedHelper.Journal.RejeuDuJournal.CleDe(pose, sid))])];

        await vm.RendreSessionInterrompueAsync(Substitute.For<DrDxt.Application.Autopilot.IRevertEnAttente>(), Substitute.For<ITweakCatalog>());

        sortis.Should().Equal($"{Id} (encore au journal)");
        journal.Lire().Should().BeEmpty();
        vm.MessageDemarrage.Should().Contain("Leviers.RendusParLeRejeu");
    }
}
