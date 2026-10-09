using System.Text.Json;
using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// 11/09/2026 — un levier garde est-il defait au demarrage suivant ?
///
/// Le vrai chemin, de bout en bout : une <see cref="GameOptimizationSession"/> (moteur, protocole
/// apparie, <see cref="KeepPolicy"/>) sur un <see cref="TweakCandidate"/> du produit, avec le journal
/// de revert reel dans un dossier temporaire ; puis un <see cref="RevertAuDemarrage"/> NEUF sur le
/// fichier que la session a laisse, exactement comme au redemarrage. Aucune entree n'est posee a la
/// main, sauf le test de compatibilite qui imite l'ancien format.
///
/// La « machine » est une valeur : le levier la capture comme origine, ecrit la cible, et son revert
/// remet ce que le snapshot porte. Un levier deja pose recapture donc la cible — le piege reel.
/// </summary>
public sealed class LevierGardeAuDemarrageTests : IDisposable
{
    private const string Chemin = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const uint Origine = 1, Cible = 2;

    private static readonly GameProfileIdentity Identite = new("Warzone.exe", null, null, null, null);

    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-garde-{Guid.NewGuid():N}");
    private string Fichier => Path.Combine(_dossier, "pending-revert.json");

    public LevierGardeAuDemarrageTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    private static string Fil(uint v) => PrivilegedOpCodec.Serialize(
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode", RegistryValueType.DWord,
            v.ToString(System.Globalization.CultureInfo.InvariantCulture))).Value!;

    private static uint Decoder(string fil) =>
        uint.Parse(((SetRegistryValueOp)PrivilegedOpCodec.Deserialize(fil).Value!).Value,
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>La machine : une seule valeur, que le levier lit, ecrit et rend.</summary>
    private sealed class Machine
    {
        public uint Valeur = Origine;
        public int Rendus;
    }

    private sealed class Levier(Machine m, string id = "latency.hagsOn") : ITweak
    {
        public string Id => id;
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak.latency.hags.enable";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;

        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            var s = new TweakSnapshot(Id, Fil(m.Valeur), Fil(Cible));   // capture PUIS ecrit
            m.Valeur = Cible;
            return Task.FromResult(Result<TweakSnapshot>.Ok(s));
        }

        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            m.Valeur = Decoder(snapshot.OriginalValueJson);
            m.Rendus++;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class Registre(Machine m) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) => Result<uint?>.Ok(m.Valeur);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    /// <summary>Le reglage ne change que le p95 : `gain` = le levier l'ameliore, sinon il l'empire.
    /// A chaque passe, un sondage : quand la cible est sur la machine, le fichier que la session a
    /// ecrit doit porter un ESSAI qui sait la rendre. Sinon une session tuee a cet instant ne serait
    /// plus rendue — c'est la raison d'etre du journal.</summary>
    private sealed class Banc(Machine m, string fichier, bool gain, List<string> violations) : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int pid, TimeSpan d, int runs, CancellationToken ct)
        {
            if (m.Valeur == Cible)
            {
                var essai = new FichierRevertEnAttente(fichier).Lire();
                if (essai.Value?.Snapshot is not { } s) violations.Add("cible posee, aucun essai au journal");
                else if (Decoder(s.OriginalValueJson) != Origine) violations.Add("essai present, mauvaise origine");
            }
            var pose = m.Valeur == Cible;
            var p95 = gain ? (pose ? 11.0 : 12.0) : (pose ? 13.0 : 12.0);
            return Task.FromResult(Result<BenchmarkResult>.Ok(new BenchmarkResult(
                100, 60, 48, new List<double> { 10.0 }, null, 1.0, runs,
                P95FrametimeMs: p95, HygieneKeptFraction: 1.0)));
        }
    }

    /// <summary>Une partie qui derive : rien n'est tente.</summary>
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

    private static CandidateDescriptor Descripteur(string id = "latency.hagsOn") =>
        new(id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, new[] { "latence" });

    private GameOptimizationProfile Session(Machine m, IRevertEnAttente journal, bool gain,
        List<string>? violations = null, IBenchmarkRunner? banc = null, string id = "latency.hagsOn")
    {
        var candidat = new TweakCandidate(new Levier(m, id), Descripteur(id), new Registre(m), journal);
        return new GameOptimizationSession(banc ?? new Banc(m, Fichier, gain, violations ?? new List<string>()), () => Identite)
            .Run(1234, new IAutopilotCandidate[] { candidat });
    }

    /// <summary>Le redemarrage : un journal NEUF sur le fichier laisse, et un catalogue qui connait le levier.</summary>
    private string? Redemarrer(Machine m, string id = "latency.hagsOn") =>
        RevertAuDemarrage.Executer(new FichierRevertEnAttente(Fichier), new TweakCatalog(new ITweak[] { new Levier(m, id) }));

    /// <summary>Les leviers gardes, lus dans le fichier brut que le produit a ecrit.</summary>
    private List<(string Id, uint Origine)> GardesDuFichier()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fichier));
        return doc.RootElement.GetProperty("Gardes").EnumerateArray()
            .Select(g => (g.GetProperty("TweakId").GetString()!,
                          Decoder(g.GetProperty("Snapshot").GetProperty("OriginalValueJson").GetString()!)))
            .ToList();
    }

    [Fact]
    public void Un_levier_GARDE_n_est_pas_rendu_au_demarrage_et_sa_valeur_d_origine_reste_ecrite()
    {
        var m = new Machine();
        var violations = new List<string>();

        var profil = Session(m, new FichierRevertEnAttente(Fichier), gain: true, violations);

        // La premisse, verifiee : le moteur a garde, et la cible est sur la machine.
        profil.Results.Should().ContainSingle().Which.Outcome.Should().Be(CandidateOutcome.Kept);
        m.Valeur.Should().Be(Cible);
        violations.Should().BeEmpty("tant que la decision n'est pas prise, une session tuee doit etre rendue");
        var rendusPendantLaSession = m.Rendus;

        var phrase = Redemarrer(m);

        phrase.Should().BeNull("rien n'a ete interrompu : dire le contraire serait mentir au joueur");
        m.Valeur.Should().Be(Cible, "le gain prouve n'est pas defait au demarrage");
        m.Rendus.Should().Be(rendusPendantLaSession, "le demarrage n'a rien rendu");
        GardesDuFichier().Should().Equal(new[] { ("latency.hagsOn", Origine) },
            "la seule trace de l'etat d'origine du joueur n'est pas effacee");
        new FichierRevertEnAttente(Fichier).Lire().Value.Should().BeNull("plus aucun essai en cours");
    }

    [Fact]
    public void Un_levier_REJETE_est_deja_rendu_et_le_journal_est_leve()
    {
        var m = new Machine();

        var profil = Session(m, new FichierRevertEnAttente(Fichier), gain: false);

        profil.Results.Single().Outcome.Should().BeOneOf(CandidateOutcome.RejectedRegression, CandidateOutcome.RejectedUnresolved);
        m.Valeur.Should().Be(Origine, "rendu pendant la session");
        File.Exists(Fichier).Should().BeFalse("rien d'essai, rien de garde : le journal est leve");
        Redemarrer(m).Should().BeNull();
        m.Valeur.Should().Be(Origine);
    }

    [Fact]
    public void Un_levier_NON_DECIDABLE_n_est_jamais_pose_et_ne_laisse_rien()
    {
        var m = new Machine();

        var profil = Session(m, new FichierRevertEnAttente(Fichier), gain: true, banc: new BancQuiDerive());

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.NotAttempted);
        m.Valeur.Should().Be(Origine);
        File.Exists(Fichier).Should().BeFalse();
        Redemarrer(m).Should().BeNull();
    }

    /// <summary>Le filet tel quel : le journal ecrit, sauf la mise a jour « garde » — exactement l'etat
    /// d'une App tuee entre la decision et cette ecriture.</summary>
    private sealed class JournalTueAvantDeGarder(IRevertEnAttente vrai, bool echoue) : IRevertEnAttente
    {
        public Result Poser(string tweakId, TweakSnapshot? snapshot) => vrai.Poser(tweakId, snapshot);
        public Result Lever() => vrai.Lever();
        public Result<RevertEnAttenteEntree?> Lire() => vrai.Lire();
        public Result Garder(string tweakId, TweakSnapshot snapshot)
            => echoue ? Result.Fail("X", "disque plein") : Result.Ok();   // ... l'ecriture n'a jamais eu lieu
    }

    [Fact]
    public void Tuee_juste_AVANT_d_ecrire_le_keep_la_session_est_rendue_au_demarrage()
    {
        var m = new Machine();

        var profil = Session(m, new JournalTueAvantDeGarder(new FichierRevertEnAttente(Fichier), echoue: false), gain: true);
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);

        var phrase = Redemarrer(m);

        phrase.Should().Contain("interrompue");
        m.Valeur.Should().Be(Origine, "le defaut sur : sans l'ecriture « garde », le levier est rendu");
        File.Exists(Fichier).Should().BeFalse();
    }

    [Fact]
    public void Un_journal_qui_REFUSE_le_keep_le_dit_et_le_levier_sera_rendu()
    {
        var m = new Machine();

        var profil = Session(m, new JournalTueAvantDeGarder(new FichierRevertEnAttente(Fichier), echoue: true), gain: true);

        profil.Results.Single().Detail.Should().Contain("sera remis comme avant au prochain démarrage");
        Redemarrer(m).Should().Contain("interrompue");
        m.Valeur.Should().Be(Origine);
    }

    [Fact]
    public void Retente_le_levier_garde_ne_perd_ni_le_gain_ni_la_vraie_origine()
    {
        // La 2e session trouve la cible deja posee : son levier la recapture comme « origine ».
        // Son A/B mesure donc la cible contre la cible (le piege connu) : aucun keep possible, et son
        // « revert » rend... la cible.
        var m = new Machine();
        Session(m, new FichierRevertEnAttente(Fichier), gain: true);
        m.Valeur.Should().Be(Cible);

        Session(m, new FichierRevertEnAttente(Fichier), gain: true).Kept.Should().BeEmpty();

        m.Valeur.Should().Be(Cible, "le gain du premier keep reste pose");
        GardesDuFichier().Should().Equal(new[] { ("latency.hagsOn", Origine) },
            "l'essai rate n'efface pas la trace du levier garde");

        // Et si un keep arrivait sur ce snapshot recapture, l'origine du premier keep resterait.
        new FichierRevertEnAttente(Fichier)
            .Garder("latency.hagsOn", new TweakSnapshot("latency.hagsOn", Fil(Cible), Fil(Cible))).IsOk.Should().BeTrue();
        GardesDuFichier().Should().Equal(new[] { ("latency.hagsOn", Origine) },
            "l'origine recapturee (la cible) n'ecrase jamais la vraie");
        Redemarrer(m).Should().BeNull();
        m.Valeur.Should().Be(Cible);
    }

    [Fact]
    public void Une_session_suivante_sur_un_AUTRE_levier_n_efface_pas_les_leviers_gardes()
    {
        var m = new Machine();
        Session(m, new FichierRevertEnAttente(Fichier), gain: true);

        var autre = new Machine();
        Session(autre, new FichierRevertEnAttente(Fichier), gain: false, id: "latency.autre");

        GardesDuFichier().Should().Equal(new[] { ("latency.hagsOn", Origine) });
        new FichierRevertEnAttente(Fichier).Lire().Value.Should().BeNull();
    }

    // ── 11/09/2026 — l'origine d'un keep entre au manifeste signe, le seul que la desinstallation lit ──

    private sealed class Manifeste(Result reponse) : IDesiredStateStore
    {
        public List<DesiredState> Commits { get; } = new();

        public Task<Result> CommitAsync(DesiredState state)
        {
            Commits.Add(state);
            return Task.FromResult(reponse);
        }

        public Task<Result<DesiredState>> LoadAsync()
            => Task.FromResult(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));
    }

    // GARDE1 (09/10/2026) : un levier gardé par la session garde son instantané (l'App l'inscrit au journal et date sa
    // pose : « Annuler » et la garde d'incident le voient) ; un levier rejeté n'en laisse aucun.
    [Fact]
    public void Garde_par_la_session_le_levier_rend_son_instantane_rejete_aucun()
    {
        var garde = SessionAvecManifeste(new Machine(), manifeste: null, gain: true);
        garde.Profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
        garde.Candidat.SnapshotPose.Should().NotBeNull();
        garde.Candidat.SnapshotPose!.TweakId.Should().Be(garde.Candidat.Descriptor.Id);

        var rejete = SessionAvecManifeste(new Machine(), manifeste: null, gain: false);
        rejete.Profil.Results.Single().Outcome.Should().NotBe(CandidateOutcome.Kept);
        rejete.Candidat.SnapshotPose.Should().BeNull();
    }

    private (GameOptimizationProfile Profil, TweakCandidate Candidat) SessionAvecManifeste(
        Machine m, IDesiredStateStore? manifeste, bool gain = true)
    {
        var c = new TweakCandidate(new Levier(m), Descripteur(), new Registre(m), new FichierRevertEnAttente(Fichier),
            manifeste: manifeste);
        var p = new GameOptimizationSession(new Banc(m, Fichier, gain, new List<string>()), () => Identite)
            .Run(1234, new IAutopilotCandidate[] { c });
        return (p, c);
    }

    [Fact]
    public void Un_keep_INSCRIT_son_origine_au_manifeste_en_RevertOnly_jamais_dans_ce_qui_est_reaffirme()
    {
        var m = new Machine();
        var manifeste = new Manifeste(Result.Ok());

        var (profil, candidat) = SessionAvecManifeste(m, manifeste);

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
        var commit = manifeste.Commits.Should().ContainSingle().Subject;
        commit.Tweaks.Should().BeEmpty("un keep de decouverte n'est pas reaffirme au demarrage");
        var inscrit = commit.RevertOnly.Should().ContainSingle().Subject;
        inscrit.TweakId.Should().Be("latency.hagsOn");
        Decoder(inscrit.OriginalValueJson).Should().Be(Origine, "l'etat du joueur AVANT le levier");
        candidat.OrigineHorsManifeste.Should().BeNull();
    }

    [Fact]
    public void Sans_manifeste_le_keep_tient_l_origine_reste_au_journal_et_c_est_DIT()
    {
        // `tools/optimize` : pas de manifeste signe dans ce processus.
        var m = new Machine();

        var (profil, candidat) = SessionAvecManifeste(m, manifeste: null);

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
        profil.Results.Single().Detail.Should().NotContain("sera remis", "le journal, lui, a bien ete ecrit");
        candidat.OrigineHorsManifeste.Should().NotBeNullOrEmpty();
        GardesDuFichier().Should().Equal(new[] { ("latency.hagsOn", Origine) });
    }

    [Fact]
    public void Un_manifeste_qui_REFUSE_ne_defait_pas_le_keep_et_le_dit()
    {
        var m = new Machine();

        var (_, candidat) = SessionAvecManifeste(m, new Manifeste(Result.Fail("X", "disque plein")));

        candidat.OrigineHorsManifeste.Should().Contain("disque plein");
        m.Valeur.Should().Be(Cible, "le gain prouve reste");
        Redemarrer(m).Should().BeNull();
    }

    [Fact]
    public void Un_levier_REJETE_n_inscrit_rien_au_manifeste()
    {
        var manifeste = new Manifeste(Result.Ok());

        SessionAvecManifeste(new Machine(), manifeste, gain: false);

        manifeste.Commits.Should().BeEmpty();
    }

    [Fact]
    public void Un_journal_de_l_ANCIEN_format_un_essai_seul_est_encore_rendu()
    {
        // Ce qu'ecrivait `Poser` avant les leviers gardes : l'entree seule, a la racine.
        var m = new Machine { Valeur = Cible };
        File.WriteAllText(Fichier, JsonSerializer.Serialize(
            new RevertEnAttenteEntree("latency.hagsOn", new TweakSnapshot("latency.hagsOn", Fil(Origine), Fil(Cible)))));

        Redemarrer(m).Should().Contain("interrompue");
        m.Valeur.Should().Be(Origine);
        File.Exists(Fichier).Should().BeFalse();
    }
}
