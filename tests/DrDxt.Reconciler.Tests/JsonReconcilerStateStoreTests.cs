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

    // Le deserialiseur remplit un etat ampute sans se plaindre (Items nul, build nul), et le
    // service SYSTEM le parcourait au demarrage. Ampute = illisible ; le reconciliateur n'ecrit alors rien
    // (voir FailClosedEndToEndTests).
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"GlobalPaused":true,"LastFeatureUpdateBuild":"26200.8655","UncleanBootStreak":2}""")]
    [InlineData("""{"GlobalPaused":false,"UncleanBootStreak":0,"Items":[]}""")]
    [InlineData("""{"GlobalPaused":false,"LastFeatureUpdateBuild":"x","UncleanBootStreak":0,"Items":[null]}""")]
    public async Task LoadAsync_WhenAmputated_Fails(string json)
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, json);
            (await new JsonReconcilerStateStore(path).LoadAsync(CancellationToken.None)).IsOk.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
}
