using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

/// <summary>
/// Le manifeste signe fusionne au lieu de remplacer. Par identifiant : ajouter, ou mettre a
/// jour la valeur voulue en GARDANT la premiere origine. Rien n'est jamais retire, et un manifeste existant
/// qu'on ne sait pas relire n'est jamais ecrase.
/// </summary>
public sealed class ManifesteFusionStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 22, 0, 0, TimeSpan.Zero);

    private static (SignedDesiredStateStore Store, InMemoryManifestStorage Stockage) Build()
    {
        var stockage = new InMemoryManifestStorage();
        var cles = new FakeKeyPair();
        var store = new SignedDesiredStateStore(stockage, cles.Signer(), cles.Verifier(),
            new FakePolicy().Map("reflex", ReconcileSurface.Registry).Map("plan", ReconcileSurface.PowerPlan),
            new FakeClock { UtcNow = At });
        return (store, stockage);
    }

    private static DesiredState Correction(string id, string voulu, string origine)
        => new(new[] { new AppliedTweak(id, voulu, origine, At) });

    private static DesiredState Garde(string id, string voulu, string origine)
        => new(Array.Empty<AppliedTweak>(), new[] { new AppliedTweak(id, voulu, origine, At) });

    [Fact]
    public async Task Un_commit_ne_retire_jamais_ce_qu_un_commit_precedent_a_arme()
    {
        var (store, _) = Build();
        (await store.CommitAsync(Correction("plan", "high", "balanced"))).IsOk.Should().BeTrue();
        (await store.CommitAsync(Correction("reflex", "2", "0"))).IsOk.Should().BeTrue();

        var lu = (await store.LoadAsync()).Value!;
        lu.Tweaks.Select(t => t.TweakId).Should().Equal("plan", "reflex");
    }

    [Fact]
    public async Task Un_levier_retente_garde_la_PREMIERE_origine_et_prend_la_nouvelle_valeur_voulue()
    {
        var (store, _) = Build();
        await store.CommitAsync(Correction("reflex", "2", "0"));
        // Retente : le levier a capture l'etat deja change comme « origine ».
        await store.CommitAsync(Correction("reflex", "3", "2"));

        var t = (await store.LoadAsync()).Value!.Tweaks.Single();
        t.OriginalValueJson.Should().Be("0", "l'origine d'avant le PREMIER changement est la seule vraie");
        t.DesiredValueJson.Should().Be("3");
    }

    [Fact]
    public async Task Un_levier_GARDE_retente_garde_sa_premiere_origine_et_une_correction_ne_l_efface_pas()
    {
        var (store, _) = Build();
        await store.CommitAsync(Garde("latency.render_queue_cap", "1", "origine"));
        await store.CommitAsync(Garde("latency.render_queue_cap", "1", "cible"));
        await store.CommitAsync(Correction("plan", "high", "balanced"));

        var lu = (await store.LoadAsync()).Value!;
        lu.RevertOnly.Should().ContainSingle().Which.OriginalValueJson.Should().Be("origine");
        lu.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("plan");
    }

    [Fact]
    public async Task Un_levier_garde_n_est_jamais_dans_ce_que_le_reconciliateur_reaffirme()
    {
        // Hors du perimetre reconciliable (HKCU composite) : inscrit pour la desinstallation, jamais
        // reaffirme au demarrage — la politique ne juge que `Tweaks`, la seule liste qu'il lit.
        var (store, _) = Build();
        (await store.CommitAsync(Garde("latency.gameModeOn", "on", "off"))).IsOk.Should().BeTrue();

        var lu = (await store.LoadAsync()).Value!;
        lu.Tweaks.Should().BeEmpty();
        lu.RevertOnly!.Single().TweakId.Should().Be("latency.gameModeOn");
    }

    [Fact]
    public async Task Un_manifeste_existant_ILLISIBLE_n_est_jamais_ecrase()
    {
        var (store, stockage) = Build();
        await store.CommitAsync(Correction("plan", "high", "balanced"));
        var altere = stockage.Slot! with { SignatureBase64 = Convert.ToBase64String(new byte[64]) };
        stockage.Slot = altere;

        var r = await store.CommitAsync(Correction("reflex", "2", "0"));

        r.IsOk.Should().BeFalse("ecraser un manifeste qu'on ne sait pas relire effacerait ses origines");
        stockage.Slot.Should().BeSameAs(altere);
    }
}
