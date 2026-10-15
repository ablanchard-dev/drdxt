using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Le journal des candidats, ecrit par une vraie session et relu depuis le disque.
///
/// Aucune ligne n'est posee a la main : chaque test fait tourner <see cref="GameOptimizationSession"/>
/// — moteur, protocole apparie, <see cref="KeepPolicy"/> — sur un banc substitue, puis relit le
/// fichier dans un dossier temporaire. Une fixture ecrite a la main prouverait que le format se
/// relit, pas que le produit l'ecrit.
/// </summary>
public sealed class JournalDesCandidatsTests : IDisposable
{
    private static readonly GameProfileIdentity Identite = new(
        "Warzone.exe", "NVIDIA RTX 4060", "616.56", "Windows 11 26200", "2560x1440 @ 200 Hz");

    private static readonly DateTimeOffset Quand = new(2026, 9, 11, 22, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Lecture = new()
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-candidats-{Guid.NewGuid():N}");

    private string Chemin => Path.Combine(_dossier, JournalDesCandidats.NomFichier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    private JournalDesCandidats Journal(string? chemin = null)
        => new(chemin ?? Chemin, () => "machine-test", () => Quand);

    private static List<CandidatJuge> Relire(string chemin)
        => File.ReadAllLines(chemin).Select(l => JsonSerializer.Deserialize<CandidatJuge>(l, Lecture)!).ToList();

    private sealed class CandidatFake : IAutopilotCandidate
    {
        public bool Applique;
        public CandidateDescriptor Descriptor { get; } = new(
            "gpu.core_offset", CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false,
            new[] { "fps.moyen", "fps.p1low" });

        public Result Apply() { Applique = true; return Result.Ok(); }
        public Result Revert() { Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }

    /// <summary>Le reglage ne change que le p95 : moyenne et 1 % low identiques dans les deux etats.
    /// C'est exactement le cas « p95 seul » que le journal existe pour compter.</summary>
    private sealed class BancP95Seul(CandidatFake c) : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
            => Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                100, 60, 48, new List<double> { 10.0 }, null, 1.0, runs,
                P95FrametimeMs: c.Applique ? 11.0 : 12.0, HygieneKeptFraction: 1.0)));
    }

    /// <summary>Une partie qui derive : rien ne peut etre tente.</summary>
    private sealed class BancQuiDerive : IBenchmarkRunner
    {
        private readonly Queue<double> _fps = new(new[] { 135.0, 140.0, 148.0 });

        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            var fps = _fps.Count > 0 ? _fps.Dequeue() : 0;
            return Task.FromResult(fps > 0
                ? Result<BenchmarkResult>.Ok(new BenchmarkResult(fps, 80, 64, new List<double> { 1000.0 / fps }, null, 1.0, runs, HygieneKeptFraction: 1.0))
                : Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        }
    }

    private GameOptimizationProfile SessionP95Seul(JournalDesCandidats journal)
    {
        var candidat = new CandidatFake();
        return new GameOptimizationSession(new BancP95Seul(candidat), () => Identite, journal)
            .Run(1234, new IAutopilotCandidate[] { candidat });
    }

    [Fact]
    public void Une_session_reelle_laisse_une_ligne_par_candidat_avec_la_provenance_de_son_keep()
    {
        var profil = SessionP95Seul(Journal());

        // La premisse, verifiee et pas supposee : le moteur a bien garde sur le p95 seul.
        var r = profil.Results.Should().ContainSingle().Subject;
        r.Outcome.Should().Be(CandidateOutcome.Kept);
        r.Queue.Should().Be(PreuveDeQueue.P95Seul);

        var ligne = Relire(Chemin).Should().ContainSingle().Subject;
        ligne.Levier.Should().Be("gpu.core_offset");
        ligne.Decision.Should().Be("garde");
        ligne.Issue.Should().Be(CandidateOutcome.Kept);
        ligne.Queue.Should().Be(PreuveDeQueue.P95Seul, "la provenance doit etre celle de la decision, recopiee");
        ligne.MachineId.Should().Be("machine-test");
        ligne.Jeu.Should().Be("Warzone.exe");
        ligne.QuandUtc.Should().Be(Quand);
        ligne.PassesAb.Should().Be(r.PassesAb).And.BeGreaterThan(0);

        // Chaque grandeur, avec son ecart et le bruit contre lequel elle a ete jugee.
        ligne.Ecarts.Should().Equal(
            r.Metriques!.Select(m => new EcartJuge(m.Metrique, m.Preuve.DeltaPct, m.Preuve.ResolutionPct, m.Preuve.Outcome)));
        ligne.Ecarts.Should().HaveCount(3);
        ligne.Ecarts.Single(e => e.Grandeur == GameMetricKind.P95Frametime).EcartPct.Should().BeApproximately(9.09, 0.01);
    }

    [Fact]
    public void La_scene_DECLAREE_entre_dans_la_ligne_telle_que_la_session_l_a_recue()
    {
        var candidat = new CandidatFake();
        var contrat = new SceneContract(SceneRepresentativity.Entrainement, SceneRole.Decouverte, "stand de tir, cibles fixes");
        new GameOptimizationSession(new BancP95Seul(candidat), () => Identite, Journal())
            .Run(1234, new IAutopilotCandidate[] { candidat }, scene: contrat);

        var ligne = Relire(Chemin).Should().ContainSingle().Subject;
        ligne.Scene.Should().Be(SceneRepresentativity.Entrainement);
        ligne.Role.Should().Be(SceneRole.Decouverte);
        ligne.DescriptionScene.Should().Be("stand de tir, cibles fixes");
    }

    [Fact]
    public void Sans_scene_declaree_la_ligne_dit_JE_NE_SAIS_PAS_et_pas_rien()
    {
        SessionP95Seul(Journal());

        var ligne = Relire(Chemin).Single();
        ligne.Scene.Should().Be(SceneRepresentativity.Unknown, "la session recoit « inconnue », et la ligne le recopie");
        ligne.Role.Should().Be(SceneRole.Decouverte);
        ligne.DescriptionScene.Should().BeNull();
    }

    [Fact]
    public void Une_ligne_ecrite_AVANT_la_scene_se_relit_scene_non_consignee()
    {
        // Une vraie ligne du produit, amputee des trois champs : la forme qu'ecrit une ancienne version
        // du journal.
        SessionP95Seul(Journal());
        var noeud = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(Chemin).Single())!.AsObject();
        noeud.Remove("Scene");
        noeud.Remove("Role");
        noeud.Remove("DescriptionScene");

        var ancienne = JsonSerializer.Deserialize<CandidatJuge>(noeud.ToJsonString(), Lecture)!;

        ancienne.Levier.Should().Be("gpu.core_offset");
        ancienne.Scene.Should().BeNull("non consignee n'est pas « inconnue »");
        ancienne.Role.Should().BeNull();
        ancienne.DescriptionScene.Should().BeNull();
    }

    // Une preuve invalide porte NaN (Evidence.Invalide), et le JSON par defaut refuse d'ecrire
    // NaN. Un seul candidat non decidable faisait perdre toutes les lignes de la session.
    [Fact]
    public void Une_preuve_invalide_se_consigne_au_lieu_de_faire_perdre_la_session()
    {
        var profil = SessionP95Seul(Journal(Path.Combine(_dossier, "premiere.jsonl")));
        var invalide = profil with
        {
            Results = [profil.Results.Single() with
            {
                Metriques = [new MetricEvidence(GameMetricKind.Moyenne, Evidence.Invalide("aucune passe de référence exploitable"))],
            }],
        };

        Journal().Consigner(invalide).IsOk.Should().BeTrue();

        var ecart = Relire(Chemin).Single().Ecarts.Single();
        ecart.EcartPct.Should().Be(double.NaN);
        ecart.Issue.Should().Be(EvidenceOutcome.Invalid);
    }

    [Fact]
    public void Le_journal_s_ajoute_il_ne_se_reecrit_jamais()
    {
        SessionP95Seul(Journal());
        var premiere = File.ReadAllLines(Chemin).Single();

        SessionP95Seul(Journal());

        var lignes = File.ReadAllLines(Chemin);
        lignes.Should().HaveCount(2);
        lignes[0].Should().Be(premiere, "la mesure la plus ancienne ne se perd pas a la suivante");
    }

    [Fact]
    public void Un_candidat_jamais_mesure_est_consigne_NON_DECIDABLE_sans_provenance()
    {
        new GameOptimizationSession(new BancQuiDerive(), () => Identite, Journal())
            .Run(1234, new IAutopilotCandidate[] { new CandidatFake() });

        var ligne = Relire(Chemin).Should().ContainSingle().Subject;
        ligne.Decision.Should().Be("non_decidable");
        ligne.Issue.Should().Be(CandidateOutcome.NotAttempted);
        ligne.Queue.Should().Be(PreuveDeQueue.Aucune);
        ligne.Ecarts.Should().BeEmpty();
    }

    [Fact]
    public void Un_journal_impossible_a_ecrire_ne_coute_pas_la_session()
    {
        // Le parent du fichier est un fichier : aucun dossier ne peut y etre cree.
        Directory.CreateDirectory(_dossier);
        var bloqueur = Path.Combine(_dossier, "bloqueur");
        File.WriteAllText(bloqueur, "x");
        var journal = Journal(Path.Combine(bloqueur, JournalDesCandidats.NomFichier));

        var profil = SessionP95Seul(journal);

        profil.Kept.Should().ContainSingle("la session rend son verdict meme quand le journal echoue");
        journal.Consigner(profil).IsOk.Should().BeFalse("l'echec se dit a qui le demande, il ne leve pas");
    }
}
