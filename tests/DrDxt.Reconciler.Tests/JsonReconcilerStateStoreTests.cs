using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// Le magasin d'état annexe d'exécution (coupe-circuits, dernier build vu, série de
// démarrages). Aller-retour de tous les champs ; un fichier absent est un défaut sain (pas un échec).
// Vraies E/S dans un dossier temporaire (sans admin) ; tenu à part du manifeste signé.
// -----------------------------------------------------------------------
public sealed class JsonReconcilerStateStoreTests
{
    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), "DrDxtState_" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task LoadAsync_WhenAbsent_ReturnsSaneDefault()
    {
        var path = TempFile();
        var store = new JsonReconcilerStateStore(path);

        var loaded = await store.LoadAsync(CancellationToken.None);
        loaded.IsOk.Should().BeTrue();
        loaded.Value.Should().Be(ReconcilerState.Default);
        loaded.Value!.Items.Should().BeEmpty();
        loaded.Value.UncleanBootStreak.Should().Be(0);
    }

    // Le helper (référence posée au commit) et le service (série de démarrages, comptabilité d'une passe) réécrivent le même
    // fichier, chacun en lisant, modifiant, réécrivant. Sans verrou commun, le second effaçait ce que le premier venait d'écrire.
    [Fact]
    public async Task Deux_mises_a_jour_qui_se_chevauchent_gardent_chacune_leur_ecriture()
    {
        var path = TempFile();
        try
        {
            IReconcilerStateStore service = new JsonReconcilerStateStore(path);
            IReconcilerStateStore helper = new JsonReconcilerStateStore(path);
            Task<Result<ReconcilerState>>? pendant = null;

            var r = await service.MettreAJourAsync(etat =>
            {
                pendant = helper.MettreAJourAsync(e => e with { LastFeatureUpdateBuild = "26200.1" }, CancellationToken.None);
                pendant.Wait(300).Should().BeFalse("le helper attend que le service ait écrit");
                return etat with { UncleanBootStreak = etat.UncleanBootStreak + 1 };
            }, CancellationToken.None);

            r.IsOk.Should().BeTrue();
            (await pendant!).IsOk.Should().BeTrue();
            var lu = (await service.LoadAsync(CancellationToken.None)).Value!;
            lu.UncleanBootStreak.Should().Be(1);
            lu.LastFeatureUpdateBuild.Should().Be("26200.1");
            File.Exists(Path.ChangeExtension(path, ".verrou")).Should().BeFalse("le verrou part avec la mise à jour");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Une_mise_a_jour_qui_ne_change_rien_n_ecrit_rien()
    {
        var path = TempFile();
        IReconcilerStateStore store = new JsonReconcilerStateStore(path);

        var r = await store.MettreAJourAsync(_ => null, CancellationToken.None);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(ReconcilerState.Default);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var path = TempFile();
        try
        {
            var store = new JsonReconcilerStateStore(path);
            var state = new ReconcilerState(
                LastFeatureUpdateBuild: "22631.3447",
                UncleanBootStreak: 2,
                LastStableSessionAt: new DateTimeOffset(2026, 6, 23, 9, 0, 0, TimeSpan.Zero),
                Items: new[]
                {
                    new ReconcilerItemState("reflex.lowlatency", true, "22631.3447", "555.99",
                        new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), "OK"),
                });

            (await store.SaveAsync(state, CancellationToken.None)).IsOk.Should().BeTrue();

            var loaded = await store.LoadAsync(CancellationToken.None);
            loaded.IsOk.Should().BeTrue();
            loaded.Value.Should().BeEquivalentTo(state);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Le deserialiseur remplit un etat ampute sans se plaindre (Items nul, build nul) : ampute = illisible.
    // Illisible pour toujours, il arrêtait le maintien à chaque démarrage, et rien ne s'écrivait plus. Il est mis de côté (sa
    // trace reste) et l'état repart du premier lancement, dit au journal du service. La série de démarrages, elle, ne se sait
    // plus : elle repart au seuil du garde, la réapplication GPU reste suspendue jusqu'à une session stable.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"GlobalPaused":true,"LastFeatureUpdateBuild":"26200.8655","UncleanBootStreak":2}""")]
    [InlineData("""{"GlobalPaused":false,"UncleanBootStreak":0,"Items":[]}""")]
    [InlineData("""{"GlobalPaused":false,"LastFeatureUpdateBuild":"x","UncleanBootStreak":0,"Items":[null]}""")]
    [InlineData("""{"GlobalPaused":fa""")]
    public async Task LoadAsync_WhenUnreadable_SetsItAsideAndStartsOverPrudently(string json)
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, json);
            var journal = new JournalFactice();

            var lu = await new JsonReconcilerStateStore(path, journal: journal).LoadAsync(CancellationToken.None);

            lu.IsOk.Should().BeTrue();
            lu.Value.Should().Be(ReconcilerState.Default with { UncleanBootStreak = BootLoopGuard.SeuilParDefaut });
            File.Exists(path).Should().BeFalse("mis de côté, pas réécrit par-dessus");
            Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".mis-de-cote-*")
                .Should().ContainSingle().Which.Should().Match(f => File.ReadAllText(f) == json);
            journal.Lignes.Should().ContainSingle().Which.Should().Contain("set-aside");
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    // L'App lit l'état du service pour l'afficher ; élevée, sa lecture mettait de côté un état abîmé, et le service repartait de
    // l'état par défaut au lieu du seuil prudent qu'il pose lui-même. Le lecteur seul le dit illisible et le laisse en place.
    [Fact]
    public async Task Un_lecteur_seul_laisse_un_etat_abime_a_sa_place()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{}");

            var lu = await new JsonReconcilerStateStore(path, lectureSeule: true).LoadAsync(CancellationToken.None);

            lu.IsOk.Should().BeFalse();
            File.ReadAllText(path).Should().Be("{}", "rien n'est renommé ni réécrit");
            Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".mis-de-cote-*").Should().BeEmpty();
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    // Une coupure de courant pendant l'écriture laisse des zéros à la place du JSON.
    [Fact]
    public async Task LoadAsync_AfterAPowerCut_FullOfZeros_StartsOverAndTheNextSaveWorks()
    {
        var path = TempFile();
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            var store = new JsonReconcilerStateStore(path);

            var lu = await store.LoadAsync(CancellationToken.None);
            lu.IsOk.Should().BeTrue();
            (await store.SaveAsync(lu.Value! with { LastFeatureUpdateBuild = "26200.8655" }, CancellationToken.None)).IsOk.Should().BeTrue();
            (await store.LoadAsync(CancellationToken.None)).Value!.LastFeatureUpdateBuild.Should().Be("26200.8655");
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    // Un fichier qu'on ne peut pas lire maintenant (tenu par un autre) n'est pas un fichier abîmé : rien n'est mis de côté,
    // la lecture échoue et le maintien n'écrit rien ce tour-ci.
    [Fact]
    public async Task LoadAsync_WhenLocked_FailsWithoutSettingAside()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{}");
            // Lecture refusée, renommage permis : seule la règle distingue « tenu » de « abîmé ».
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                (await new JsonReconcilerStateStore(path).LoadAsync(CancellationToken.None)).IsOk.Should().BeFalse();
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    private sealed class JournalFactice : IDiagnosticsLog
    {
        public List<string> Lignes { get; } = [];
        public void Change(string tweakId, string from, string to, string actor) => Lignes.Add($"{tweakId} {from} {to} {actor}");
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(""));
    }

    [Fact]
    public async Task LoadAsync_TheReferenceMachineRealState_StillLoads()
    {
        // Releve le 26/09/2026 dans %ProgramData%\DrDXT\reconciler-state.json, ecrit le 02/07.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """
                {
                  "GlobalPaused": false,
                  "LastFeatureUpdateBuild": "26200.8655",
                  "UncleanBootStreak": 0,
                  "LastStableSessionAt": "2026-07-01T22:31:44.1451135+00:00",
                  "Items": []
                }
                """);
            var lu = await new JsonReconcilerStateStore(path).LoadAsync(CancellationToken.None);
            lu.IsOk.Should().BeTrue();
            lu.Value!.LastFeatureUpdateBuild.Should().Be("26200.8655");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    // %ProgramData% laisse tout compte créer un fichier avant l'installation, et la réparation de la racine ne descend pas :
    // un état qui n'est pas de confiance n'est pas de DrDXT. Ignoré, il vaut le premier lancement ; le prochain
    // enregistrement le remplace.
    [Fact]
    public async Task LoadAsync_WhenUntrusted_IgnoresTheFile()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{"GlobalPaused":true,"LastFeatureUpdateBuild":"x","UncleanBootStreak":9,"Items":[]}""");
            var lu = await new JsonReconcilerStateStore(path, fiable: _ => false).LoadAsync(CancellationToken.None);
            lu.IsOk.Should().BeTrue();
            lu.Value.Should().Be(ReconcilerState.Default);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Deux éléments du même levier faisaient lever ToDictionary dans la passe et l'amorçage de la référence : le service
    // SYSTEM s'arrêtait à chaque démarrage. Un seul est gardé, en pause si l'un l'est (le coupe-circuit fait faire moins).
    [Fact]
    public async Task LoadAsync_WithARepeatedLever_KeepsOneAndItsPause()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """
                {"GlobalPaused":false,"LastFeatureUpdateBuild":"x","UncleanBootStreak":0,"Items":[
                  {"TweakId":"a","Paused":false,"LastSeenBuild":"1"},
                  {"TweakId":"a","Paused":true,"LastSeenBuild":"2"},
                  {"TweakId":"b","Paused":false}]}
                """);
            var lu = await new JsonReconcilerStateStore(path).LoadAsync(CancellationToken.None);

            lu.IsOk.Should().BeTrue();
            lu.Value!.Items.Select(i => (i.TweakId, i.Paused)).Should().Equal(("a", true), ("b", false));
            lu.Value.Items.ToDictionary(i => i.TweakId, StringComparer.Ordinal).Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
