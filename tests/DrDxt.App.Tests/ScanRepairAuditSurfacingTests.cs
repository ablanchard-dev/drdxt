using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>
/// Ce que l'audit montre, et ce qu'il ne cache pas.
///
/// Ces tests existent a cause du defaut du 09/09/2026 : `RepairAuditService` et les dix types
/// qu'il collecte etaient prouves par leurs propres tests, et references par aucune couche que
/// l'utilisateur rencontre. Des tests verts sur du code que personne n'atteint ne prouvent rien
/// sur le produit — ceux-ci portent sur le VM de l'ecran, pas sur le lecteur.
/// </summary>
public sealed class ScanRepairAuditSurfacingTests
{
    private static RepairFinding Constat(
        string nom,
        RepairStatus statut,
        RepairAction action = RepairAction.Aucune,
        string? valeur = "lue")
        => new("POWER", nom, valeur, null, statut, "parce que.", action);

    /// <summary>
    /// Le cas exact qui a ete manque. Le minimum du processeur a 100 % est decrit dans ce depot
    /// comme la signature la plus courante des scripts d'optimisation. Il etait lu, il etait
    /// compte, et il n'atteignait pas l'ecran : un `NonDefault` sans geste tombe dans « verifies »,
    /// exactement au meme endroit qu'un reglage identique au defaut. Le brancher ne suffisait pas,
    /// il fallait qu'il ARRIVE.
    ///
    /// Et il arrive sans etre declare nuisible : la doctrine « different n'est pas endommage »
    /// interdit d'accuser un choix d'utilisateur, elle n'a jamais impose de le taire.
    /// </summary>
    /// <summary>
    /// Ce que le produit ne sait pas lire doit se voir, pas se compter. Jusqu'au 09/09/2026 au
    /// soir, un constat `Unknown` ne sortait que comme un nombre dans la phrase de resume, alors
    /// que `tools/repairaudit` le nommait sous « MES ANGLES MORTS ». Le meme triage rendait donc
    /// deux verites selon qu'on le regardait par l'outil ou par l'ecran — et c'est l'ecran, le seul
    /// que le client ouvre, qui en disait le moins.
    /// </summary>
    [Fact]
    public void Un_etat_NON_LU_est_nomme_a_l_ecran_et_pas_seulement_compte()
    {
        var triage = RepairTriage.Trier(new[]
        {
            Constat("illisible", RepairStatus.Unknown, valeur: null),
            Constat("suspect", RepairStatus.Suspicious),
        });

        var anglesMorts = triage.NonLus.Select(c => new RepairFindingViewModel(c)).ToList();

        anglesMorts.Should().ContainSingle().Which.Nom.Should().Be("Illisible");
        // Et il ne se melange pas a ce qui merite correction : un trou de lecture n'est pas un defaut.
        triage.Attention.Should().ContainSingle().Which.Nom.Should().Be("suspect");
    }

    [Fact]
    public void Un_etat_sain_ne_rejoint_NI_les_constats_NI_les_angles_morts()
    {
        var triage = RepairTriage.Trier(new[] { Constat("sain", RepairStatus.ExpectedDefault) });

        triage.Attention.Should().BeEmpty();
        triage.NonLus.Should().BeEmpty("un etat lu et conforme n'est pas un angle mort");
        triage.Verifies.Should().ContainSingle();
    }

    [Fact]
    public void Un_ecart_au_defaut_arrive_a_l_ecran_sans_etre_declare_nuisible()
    {
        var minimumProcesseurA100 = new RepairFinding(
            "POWER", "minimum du processeur", "100%", "5%",
            RepairStatus.NonDefault,
            "ce plan ne porte plus la valeur que Windows y met.",
            RepairAction.Recommandation);

        var triage = RepairTriage.Trier(new[] { minimumProcesseurA100 });
        var vus = triage.Attention.Select(c => new RepairFindingViewModel(c)).ToList();

        Assert.Contains(vus, v => v.Nom == "Minimum du processeur");
        Assert.Empty(triage.Verifies);
        // Il est montre, il n'est pas accuse : aucune reparation automatique n'est proposee.
        Assert.NotEqual(RepairStatus.KnownHarmful, minimumProcesseurA100.Statut);
        Assert.NotEqual(RepairAction.ReparationAutomatique, minimumProcesseurA100.Action);
    }

    [Fact]
    public void Un_ecart_SANS_geste_reste_invisible_et_c_est_pourquoi_le_geste_existe()
    {
        // Le meme constat prive de son action : il retombe dans « verifies », compte et jamais vu.
        var muet = Constat("minimum du processeur", RepairStatus.NonDefault);

        var triage = RepairTriage.Trier(new[] { muet });

        Assert.Empty(triage.Attention);
        Assert.Single(triage.Verifies);
    }

    [Fact]
    public void Le_VM_expose_ce_qui_merite_l_attention_et_pas_le_reste()
    {
        var triage = RepairTriage.Trier(new[]
        {
            Constat("nuisible", RepairStatus.KnownHarmful),
            Constat("suspect", RepairStatus.Suspicious),
            Constat("sain", RepairStatus.ExpectedDefault),
            Constat("illisible", RepairStatus.Unknown, valeur: null),
        });

        var vus = triage.Attention.Select(c => new RepairFindingViewModel(c)).ToList();

        Assert.Equal(2, vus.Count);
        Assert.Contains(vus, v => v.Nom == "Nuisible");
        Assert.Contains(vus, v => v.Nom == "Suspect");
        // L'etat sain et l'etat illisible ne remontent pas dans la liste : ils sont comptes.
        Assert.DoesNotContain(vus, v => v.Nom == "Sain");
        Assert.DoesNotContain(vus, v => v.Nom == "Illisible");
    }

    [Fact]
    public void Un_etat_non_lu_se_dit_au_lieu_de_passer_pour_une_absence_de_probleme()
    {
        var vu = new RepairFindingViewModel(
            Constat("mmcss", RepairStatus.Unknown, RepairAction.Recommandation, valeur: null));

        Assert.False(vu.ValeurLue);
        Assert.Equal("non lisible", vu.ValeurCourante);
        // La mutation que ce test empeche : rendre `string.Empty` pour une valeur nulle. Rien
        // ne leverait, l'ecran afficherait une case vide, et un angle mort passerait pour un
        // etat sain.
        Assert.NotEqual(string.Empty, vu.ValeurCourante);
    }

    [Fact]
    public void La_phrase_nomme_les_trois_nombres_meme_quand_rien_ne_demande_l_attention()
    {
        var triage = RepairTriage.Trier(new[]
        {
            Constat("sain", RepairStatus.ExpectedDefault),
            Constat("illisible", RepairStatus.Unknown, valeur: null),
        });

        Assert.Empty(triage.Attention);
        // « rien a signaler » sans le nombre d'etats non lus ferait passer un audit partiel
        // pour un audit complet. C'est la raison d'etre de cette phrase.
        Assert.Contains("1", triage.Phrase);
        Assert.Contains("1 n'a pas pu être lu", triage.Phrase);
    }

    // ── Le scan complet ───────────────────────────────────────────────────────────────────
    // Les quatre tests ci-dessus portent sur des types isoles — exactement le genre de test
    // qui etait deja vert le 09/09 pendant que rien n'atteignait l'ecran. Ceux qui suivent font
    // tourner le vrai `ScanCommand` et regardent ce que l'ecran recoit.

    // 23/09/2026 — le journal de sante gelait l'ecran. Mesure par la sonde de gel (DEBUG) sur la
    // machine de reference, jalons a l'appui : apres le retour de l'audit, le triage prend 1 ms,
    // le remplissage des listes 2 ms, et l'ecriture + comparaison de la photo du jour 390 a 400 ms
    // -- sur le fil d'interface. C'etaient les ~350 ms ou le scanner s'arretait de bouger.
    //
    // Le test tourne dans un vrai Dispatcher WPF (les continuations reviennent sur un fil, comme
    // dans l'app) : sans lui, xUnit reprend sur le pool et le defaut serait invisible.
    [Fact]
    public void Le_journal_de_sante_s_ecrit_hors_du_fil_d_interface()
    {
        int filUi = 0, filJournal = 0;
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                filUi = Environment.CurrentManagedThreadId;

                var vm = Vm(() => new[] { Constat("sain", RepairStatus.ExpectedDefault) },
                    _ => { filJournal = Environment.CurrentManagedThreadId; return null; });

                var cadre = new System.Windows.Threading.DispatcherFrame();
                var scan = vm.ScanCommand.ExecuteAsync(null);
                scan.ContinueWith(_ => dispatcher.BeginInvoke(() => cadre.Continue = false), TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(cadre);
                if (scan.IsFaulted) erreur = scan.Exception;
            }
            catch (Exception e) { erreur = e; }
        });
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        fil.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("le scan doit finir");

        erreur.Should().BeNull();
        filJournal.Should().NotBe(0, "temoin : le journal doit avoir ete appele");
        filJournal.Should().NotBe(filUi,
            "ecrire et comparer la photo du jour prend ~400 ms de disque : sur le fil d'interface, "
            + "c'est le scanner qui s'arrete de bouger");
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ScanViewModel Vm(
        Func<IReadOnlyList<RepairFinding>>? audit,
        Func<IReadOnlyList<RepairFinding>, DrDxt.Infrastructure.Repair.JournalDeSante?>? journal = null)
    {
        var st = new SettingsState(TriState.On, TriState.On,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On,
            Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), st,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) },
              68, bl, new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k, repairAudit: audit, journalSante: journal);
    }

    [Fact]
    public async Task Un_scan_fait_arriver_l_audit_a_l_ecran()
    {
        var vm = Vm(() => new[]
        {
            Constat("nuisible", RepairStatus.KnownHarmful),
            Constat("sain", RepairStatus.ExpectedDefault),
        });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.True(vm.HasRepairFindings);
        Assert.Single(vm.RepairFindings);
        Assert.Equal("Nuisible", vm.RepairFindings[0].Nom);
        Assert.False(string.IsNullOrWhiteSpace(vm.RepairSummary));
    }

    [Fact]
    public async Task Sans_audit_le_scan_n_affirme_RIEN_sur_la_sante()
    {
        var vm = Vm(audit: null);

        await vm.ScanCommand.ExecuteAsync(null);

        // La distinction que ce test protege : « pas d'audit » n'est pas « audit vide ».
        // Une phrase de resume ici ferait croire au joueur qu'on a regarde sa machine.
        Assert.Null(vm.RepairSummary);
        Assert.False(vm.HasRepairFindings);
    }

    [Fact]
    public async Task Un_audit_en_panne_degrade_la_section_sans_casser_le_scan()
    {
        var vm = Vm(() => throw new InvalidOperationException("WMI intermittent"));

        await vm.ScanCommand.ExecuteAsync(null);

        // Le scan aboutit — l'audit est ADDITIF.
        Assert.Equal(ScanState.Done, vm.State);
        // Et il ne laisse aucune phrase derriere : un resume rescape d'une collecte en panne
        // mentirait sur ce qui a ete regarde.
        Assert.Null(vm.RepairSummary);
        Assert.Empty(vm.RepairFindings);
    }

    [Fact]
    public async Task Un_second_scan_ne_cumule_pas_les_constats()
    {
        var vm = Vm(() => new[] { Constat("nuisible", RepairStatus.KnownHarmful) });

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(vm.RepairFindings);
    }

    // ── Le journal qu'on ne sait pas lire ─────────────────────────────────────────────────
    // `RepairHistoryStore.Lire()` distingue soigneusement « aucun journal » d'un journal
    // ILLISIBLE — et les deux arrivaient a l'ecran sous la meme forme : rien. Un `Fail` remontait
    // en `null` depuis `EnregistrerEtComparer`, exactement comme une premiere photo, et le joueur
    // lisait une absence de changement rassurante la ou le produit ne savait pas lire son passe.
    // Ces deux tests traversent le vrai magasin, sur un vrai fichier, jusqu'au VM.

    private static ScanViewModel VmAvecJournal(string chemin) => Vm(
        () => new[] { Constat("sain", RepairStatus.ExpectedDefault) },
        constats => new DrDxt.Infrastructure.Repair.RepairJournalService(
            new DrDxt.Infrastructure.Repair.RepairHistoryStore(chemin),
            () => null).EnregistrerEtComparer(constats));

    [Fact]
    public async Task Un_historique_ILLISIBLE_se_dit_a_l_ecran_au_lieu_de_passer_pour_aucun_changement()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");
        File.WriteAllText(chemin, "{ ceci n'est pas du JSON");
        try
        {
            // La premisse s'affirme avant de servir — et ce test ne le faisait pas.
            // Mesure du 11/09/2026 : en remplacant la propagation du `Fail` par un `throw`, les
            // QUINZE tests de ce fichier restaient VERTS. Le `catch` generique du VM ecrit lui aussi
            // « non lu » et « inconnu » : les deux seuls mots que ce test cherchait. Il pouvait donc
            // passer alors que le service mourait au lieu de propager — vert par accident, pas par
            // preuve. On affirme d'abord que le magasin echoue vraiment sur ce fichier, et on ne
            // dereference `Error` qu'apres l'avoir dit.
            var lu = new DrDxt.Infrastructure.Repair.RepairHistoryStore(chemin).DernierePhoto();
            Assert.False(lu.IsOk, "sans un magasin en echec, ce test ne mesure rien du tout");
            Assert.Contains("illisible", lu.Error!.Value.Message);

            var vm = VmAvecJournal(chemin);

            await vm.ScanCommand.ExecuteAsync(null);

            // La section doit s'ouvrir alors qu'aucun changement n'est liste : c'est tout le
            // sujet. Elle etait masquee par `RepairChanges.Count > 0`, avertissement compris.
            Assert.True(vm.HasRepairChanges);
            Assert.Empty(vm.RepairChanges);
            Assert.NotNull(vm.RepairChangesWarning);
            Assert.Contains("non lu", vm.RepairChangesWarning!);
            Assert.Contains("inconnu, pas vide", vm.RepairChangesWarning!);
            // Le mot qui n'appartient qu'au magasin. « non lu » et « inconnu » sont ecrits aussi
            // par le `catch` du VM ; « illisible » ne peut venir que de la phrase du magasin,
            // remontee par le service. C'est la seule assertion de ce test qui distingue le chemin
            // vise d'une panne rattrapee une couche plus haut.
            Assert.Contains("illisible", vm.RepairChangesWarning!);
        }
        finally { try { File.Delete(chemin); } catch { } }
    }

    [Fact]
    public async Task Une_PREMIERE_photo_n_avertit_de_RIEN_et_la_section_reste_fermee()
    {
        // La mutation que ce test empeche : avertir dans tous les cas. Un avertissement qui sort
        // aussi quand tout va bien est un avertissement qu'on cesse de lire — et le produit
        // accuserait un journal parfaitement sain de ne pas avoir ete lu.
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");
        try
        {
            var vm = VmAvecJournal(chemin);

            await vm.ScanCommand.ExecuteAsync(null);

            Assert.Null(vm.RepairChangesWarning);
            Assert.False(vm.HasRepairChanges);
            Assert.True(File.Exists(chemin), "la premiere photo doit quand meme etre enregistree");
        }
        finally { try { File.Delete(chemin); } catch { } }
    }

    [Fact]
    public async Task Un_journal_EN_PANNE_se_dit_aussi_au_lieu_de_faire_disparaitre_la_section()
    {
        // Le meme defaut par l'autre porte : une exception cote journal vidait la section sans
        // rien dire, pendant que le reste de l'audit s'affichait normalement.
        var vm = Vm(
            () => new[] { Constat("sain", RepairStatus.ExpectedDefault) },
            _ => throw new InvalidOperationException("disque occupe"));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RepairSummary);   // l'audit, lui, a bien abouti
        Assert.True(vm.HasRepairChanges);
        Assert.Contains("non lu", vm.RepairChangesWarning!);
    }

    [Fact]
    public void Seul_un_etat_nuisible_est_affiche_comme_grave()
    {
        Assert.Equal(Severity.Major, new RepairFindingViewModel(
            Constat("x", RepairStatus.KnownHarmful)).Severity);
        Assert.Equal(Severity.Minor, new RepairFindingViewModel(
            Constat("x", RepairStatus.Suspicious)).Severity);
        // « Different » n'est pas « endommage » : un ecart au defaut ne se peint pas en rouge.
        Assert.Equal(Severity.Info, new RepairFindingViewModel(
            Constat("x", RepairStatus.NonDefault)).Severity);
        Assert.Equal(Severity.Info, new RepairFindingViewModel(
            Constat("x", RepairStatus.Unknown, valeur: null)).Severity);
    }

    [Fact]
    public async Task Un_changement_de_jugement_se_lit_en_mots_et_pas_en_noms_de_code()
    {
        // 16/09/2026, capture d'écran : « POWER · plan d'alimentation actif : ExpectedDefault →
        // UserChoiceProbable — le jugement du produit a change : ExpectedDefault → UserChoiceProbable ».
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-journal-{Guid.NewGuid():N}.json");
        try
        {
            var statut = RepairStatus.ExpectedDefault;
            var vm = Vm(() => new[] { Constat("plan d'alimentation actif", statut) },
                constats => new DrDxt.Infrastructure.Repair.RepairJournalService(
                    new DrDxt.Infrastructure.Repair.RepairHistoryStore(chemin), () => null).EnregistrerEtComparer(constats));
            await vm.ScanCommand.ExecuteAsync(null);
            statut = RepairStatus.UserChoiceProbable;
            await vm.ScanCommand.ExecuteAsync(null);

            var ligne = Assert.Single(vm.RepairChanges);
            Assert.Contains("valeur par défaut → choix probable de ta part", ligne);
            Assert.DoesNotContain("ExpectedDefault", ligne);
            Assert.DoesNotContain("UserChoiceProbable", ligne);
        }
        finally
        {
            if (File.Exists(chemin)) File.Delete(chemin);
        }
    }
}
