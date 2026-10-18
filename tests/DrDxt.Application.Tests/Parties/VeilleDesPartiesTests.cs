using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// La veille. Pendant une partie, une capture courte à intervalle ; quand le jeu se ferme,
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

    // L'option de développement des images reçoit, à la clôture, la partie et ses fenêtres telles que le relevé les a vues.
    [Fact]
    public async Task Une_partie_close_passe_ses_fenetres_a_l_option_des_images()
    {
        var etats = new Queue<(string, int)?>([null, ("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var gardees = new List<(PartieRelevee Partie, int Fenetres)>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: _ => fin.Cancel(),
            patienter: (_, _) => Task.CompletedTask,
            garderLesImages: (p, c) => gardees.Add((p, c.Count)));

        await veille.VeillerAsync(fin.Token);

        gardees.Should().ContainSingle().Which.Fenetres.Should().Be(3);
        gardees[0].Partie.Jeu.Should().Be("cod.exe");
    }

    // L'option de développement ne fait jamais tomber le relevé, quelle que soit l'exception qu'elle lève : la veille continue
    // et la partie suivante s'enregistre.
    [Fact]
    public async Task Une_exception_inattendue_de_l_option_des_images_n_arrete_pas_le_releve()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 1), ("cod.exe", 1), null, ("cod.exe", 2), ("cod.exe", 2), null]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); if (enregistrees.Count == 2) fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            garderLesImages: (_, _) => throw new ArgumentException("chemin invalide"));

        await veille.Invoking(v => v.VeillerAsync(fin.Token)).Should().NotThrowAsync();
        enregistrees.Should().HaveCount(2, "la seconde partie s'enregistre malgré l'échec de l'option sur la première");
    }

    // Le jeu fermé puis relancé entre deux regards garde son nom mais pas son processus ; ce sont
    // deux parties.
    [Fact]
    public async Task Un_jeu_relance_entre_deux_regards_fait_deux_parties()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 43), ("cod.exe", 43), null]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        // La suite épuisée arrête la veille : sans ce garde, un code qui ne ferait qu'une partie tournerait sans fin
        // (mesuré par mutation : 50 minutes sur un cœur, pendant une vraie partie).
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

    // La fréquence des cœurs rapides et les watts du paquet, relevés à chaque capture.
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

    // La mémoire vidéo occupée à chaque capture ; la partie garde son pic (une saturation fait des
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

    // Les programmes qui chargent le processeur à côté du jeu. Un seul relevé au-dessus du seuil est un
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

    // La température du processeur se lit chaque seconde pendant la fenêtre ; la partie garde ce pic, pas la
    // lecture d'après coup (relevé : « 100 °C » n'était qu'une des six lectures d'une partie de 17 minutes).
    [Fact]
    public async Task La_chaleur_du_processeur_est_le_pic_lu_pendant_la_fenetre()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        var actives = new Queue<bool?>([true, false, null]);
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            processeur: () => (4900.0, 110.0, 80.0),
            temperatureProcesseur: () => 97.0,
            entrees: () => actives.Count > 0 ? actives.Dequeue() : null);

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].CelsiusMax.Should().Be(97, "le pic de la fenêtre, pas la lecture d'après la capture");
        // Sans images (Capture() n'en porte pas), les minutes jouées ne s'affirment pas.
        enregistrees[0].MinutesJouees.Should().BeNull();
    }

    // Chaque seconde porte aussi le cœur 0 et les raisons de freiner ; la partie les garde ensemble.
    [Fact]
    public async Task Le_freinage_du_processeur_se_releve_seconde_par_seconde()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: async (_, _, ct) => { await Task.Delay(1500, ct); return Capture(120); },
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            etatProcesseur: () => new EtatCpu(98, 77, false, false, false, true));

        await veille.VeillerAsync(fin.Token);

        var f = enregistrees[0].Freinage!;
        f.Secondes.Should().BeGreaterThan(0);
        f.LimiteCourant.Should().Be(f.Secondes);
        f.Chaleur.Should().Be(0);
        f.ChaudMoyenC.Should().Be(98);
        f.Coeur0MoyenC.Should().Be(77);
    }

    // Aucune seconde du processeur lue pendant une partie se dit, une seule fois par partie.
    [Fact]
    public async Task Un_processeur_illisible_en_partie_se_dit_une_fois()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), ("cod.exe", 42), null]);
        var signalees = new List<string>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(Capture(120)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: _ => fin.Cancel(),
            patienter: (_, _) => Task.CompletedTask,
            signalerEchec: signalees.Add,
            etatProcesseur: () => null);

        await veille.VeillerAsync(fin.Token);

        signalees.Should().ContainSingle().Which.Should().Contain("état du processeur non lu");
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
        // Une ratée puis deux bonnes : une partie demande deux captures. Avec une seule bonne, rien
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
    // Une capture ratée (service de mesure refusé, jeu introuvable) ne compte pas, mais
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

    // La température de la carte à chaque capture ; la partie garde son pic. Un relevé non lu ou qui
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
