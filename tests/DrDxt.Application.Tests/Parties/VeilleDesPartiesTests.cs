using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 29/09/2026 (A3, 2/3) : la veille. Pendant une partie, une capture courte à intervalle ; quand le jeu se ferme,
// la partie est résumée et enregistrée. Hors partie, rien n'est mesuré.
public sealed class VeilleDesPartiesTests
{
    private static BenchmarkResult Capture(double fps) => new(fps, fps * 0.7, fps * 0.5, [], null, 1, 1);

    [Fact]
    public async Task Une_partie_capturee_puis_fermee_est_enregistree()
    {
        // Relevés successifs du jeu : absent, en partie trois fois, puis fermé.
        var etats = new Queue<(string, int)?>([null, ("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var captures = 0;
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (pid, _, _) => { captures++; return Task.FromResult<BenchmarkResult?>(Capture(120 + captures)); },
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask);

        await veille.VeillerAsync(fin.Token);

        captures.Should().Be(3);
        enregistrees.Should().ContainSingle();
        enregistrees[0].Jeu.Should().Be("cod.exe");
        enregistrees[0].Captures.Should().Be(3);
        enregistrees[0].Goulot.Should().Be(BottleneckState.CpuBoundOrAppBound);
    }

    // 07/10/2026 (vu en vrai) : le jeu fermé puis relancé entre deux regards garde son nom mais pas son processus ; ce sont
    // deux parties.
    [Fact]
    public async Task Un_jeu_relance_entre_deux_regards_fait_deux_parties()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 43), ("cod.exe", 43), null]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        // La suite épuisée arrête la veille : sans ce garde, un code qui ne ferait qu'une partie tournerait sans fin
        // (une mutation l'a fait tourner 50 minutes sur un cœur le 07/10, pendant une vraie partie).
        var veille = new VeilleDesParties(
            jeu: () => { if (etats.Count == 0) fin.Cancel(); return etats.Count > 0 ? etats.Dequeue() : null; },
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: enregistrees.Add,
            patienter: (_, _) => Task.CompletedTask);

        await veille.VeillerAsync(fin.Token);

        enregistrees.Should().HaveCount(2);
        enregistrees.Should().OnlyContain(p => p.Captures == 2);
    }

    // 04/10/2026 (plan 1.1) : la fréquence des cœurs rapides et les watts du paquet, relevés à chaque capture.
    [Fact]
    public async Task Chaque_capture_porte_la_frequence_et_les_watts_du_processeur()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var releves = new Queue<(double?, double?, double?)>([(5100.0, 130.0, 78.0), (4300.0, 125.0, 96.0), (4200.0, 120.0, null)]);
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            processeur: () => releves.Dequeue());

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].MhzMedian.Should().Be(4300);
        enregistrees[0].WattsMedian.Should().Be(125);
        enregistrees[0].CelsiusMax.Should().Be(96, "la chaleur se juge à son pic ; un relevé absent n'est pas un zéro");
    }

    // 04/10/2026 : la mémoire vidéo occupée à chaque capture ; la partie garde son pic (une saturation fait des
    // à-coups même si elle ne dure pas).
    [Fact]
    public async Task La_partie_garde_le_pic_de_memoire_video()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var vram = new Queue<double?>([70.0, 97.0, null]);
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.FramePacing,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            memoireVideo: () => vram.Dequeue());

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].VramPourcentMax.Should().Be(97);
    }

    // 04/10/2026 : les programmes qui chargent le processeur à côté du jeu. Un seul relevé au-dessus du seuil est un
    // pic (une mise à jour qui passe) ; au moins deux, c'est un voisin de la partie, gardé avec son pic.
    [Fact]
    public async Task La_partie_garde_les_voisins_lourds_vus_au_moins_deux_fois()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var lus = new Queue<IReadOnlyList<(string, double)>?>([
            [("chrome", 40), ("obs64", 30), ("Discord", 10)],
            [("chrome", 30), ("TiWorker", 90), ("chrome", 25), ("Discord", 12)],
            null]);
        int? pidVu = null;
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.FramePacing,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            voisins: pid => { pidVu = pid; return lus.Dequeue(); });

        await veille.VeillerAsync(fin.Token);

        pidVu.Should().Be(42, "le jeu lui-même n'est pas un voisin");
        enregistrees[0].Voisins.Should().ContainSingle()
            .Which.Should().Be(new VoisinEnPartie("chrome", 55, 2));
    }

    [Fact]
    public async Task Hors_partie_rien_n_est_mesure()
    {
        var tours = 0;
        using var fin = new CancellationTokenSource();
        var captures = 0;

        var veille = new VeilleDesParties(
            jeu: () => { if (++tours > 5) fin.Cancel(); return null; },
            capturer: (_, _, _) => { captures++; return Task.FromResult<BenchmarkResult?>(Capture(100)); },
            classer: _ => BottleneckState.GpuBound,
            enregistrer: _ => { },
            patienter: (_, _) => Task.CompletedTask);

        await veille.VeillerAsync(fin.Token);

        captures.Should().Be(0, "témoin : sans jeu, aucune capture");
    }

    [Fact]
    public async Task Une_capture_ratee_ne_compte_pas_dans_la_partie()
    {
        // Une ratée puis deux bonnes : une partie demande deux captures (revue F-5, 06/10). Avec une seule bonne, rien
        // n'était enregistré, la fin n'arrivait jamais et ce test tournait sans fin.
        var etats = new Queue<(string, int)?>([("cod.exe", 7), ("cod.exe", 7), ("cod.exe", 7), null]);
        var enregistrees = new List<PartieRelevee>();
        var n = 0;
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult(++n == 1 ? null : (BenchmarkResult?)Capture(90)),
            classer: _ => BottleneckState.GpuBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask);

        await veille.VeillerAsync(fin.Token);

        enregistrees.Single().Captures.Should().Be(2, "la capture ratée ne compte pas");
    }
    // 07/10/2026 (revue F-10) : une capture ratée (service de mesure refusé, jeu introuvable) ne compte pas, mais
    // elle se dit : sa raison part au signalement, et un résultat sans image aussi.
    [Fact]
    public async Task Une_capture_ratee_se_signale_avec_sa_raison()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42)]);
        var resultats = new Queue<Func<BenchmarkResult?>>([() => throw new InvalidOperationException("service refusé"), () => Capture(0)]);
        var signales = new List<string>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult(resultats.Dequeue()()),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: _ => { },
            patienter: (_, _) => { if (etats.Count == 0) fin.Cancel(); return Task.CompletedTask; },
            signalerEchec: signales.Add);

        await veille.VeillerAsync(fin.Token);

        signales.Should().Equal("cod.exe : service refusé", "cod.exe : aucune image lue");
    }

    // 07/10/2026 (A5) : la température de la carte à chaque capture ; la partie garde son pic. Un relevé non lu ou qui
    // lève n'est pas un zéro et n'annule pas la capture.
    [Fact]
    public async Task La_partie_garde_le_pic_de_temperature_de_la_carte()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var carte = new Queue<Func<double?>>([() => 64.0, () => throw new InvalidOperationException("pilote"), () => null]);
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.GpuBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            carte: () => carte.Dequeue()());

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].Captures.Should().Be(3);
        enregistrees[0].CarteCelsiusMax.Should().Be(64);
    }

    [Fact]
    public void Une_carte_jamais_lue_reste_non_lue()
    {
        var t = DateTimeOffset.Now;
        ReleveDesParties.Resumer("cod.exe", [new(t, 120, 90, BottleneckState.GpuBound), new(t, 121, 91, BottleneckState.GpuBound)])!
            .CarteCelsiusMax.Should().BeNull();
    }
}
