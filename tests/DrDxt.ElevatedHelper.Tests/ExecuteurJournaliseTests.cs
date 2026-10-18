using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Journal;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le décorateur du journal d'écriture anticipée : avant une écriture journalisée, la valeur d'avant de sa cible est lue et
// notée sur le disque ; ce qui ne se note pas ne s'exécute pas. Les deux entrées du helper lui passent l'appelant.
public sealed class ExecuteurJournaliseTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-jea-exec-{Guid.NewGuid():N}");
    private string Fichier => Path.Combine(_dossier, "ecritures-en-attente.json");

    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } }

    private static readonly SetRegistryValueOp Hags =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2");
    private static readonly AppelantDuJournal App = new("S-1-5-21-1-2-3-1001", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly CallerInfo Pair = new(4242);
    private static readonly AppelantDuJournal Autre = new("S-1-5-21-1-2-3-1002", 5151, new DateTimeOffset(2026, 10, 18, 9, 5, 0, TimeSpan.Zero));

    private static PoseEnAttenteDOrigineOp Marquee(PrivilegedOp op) => new(PrivilegedOpCodec.Serialize(op).Value!);
    private static AcquitterEcritureOp Acquit(PrivilegedOp op) => new(PrivilegedOpCodec.Serialize(op).Value!);

    /// <summary>L'exécuteur doublé : il note ce que le journal contenait au moment où il a été appelé.</summary>
    private sealed class Interne(JournalDEcritureAnticipee journal, Result reponse) : IPrivilegedOpExecutor
    {
        public List<PrivilegedOp> Executees { get; } = [];
        public int EntreesAuMomentDExecuter { get; private set; } = -1;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Executees.Add(op);
            EntreesAuMomentDExecuter = journal.EnAttente().Value?.Count ?? -1;
            return Task.FromResult(reponse);
        }
    }

    private (ExecuteurJournalise Executeur, Interne Interne, JournalDEcritureAnticipee Journal) Monter(
        Result? reponse = null, Result<string?>? image = null, AppelantDuJournal? appelant = null)
    {
        var journal = new JournalDEcritureAnticipee(Fichier);
        var interne = new Interne(journal, reponse ?? Result.Ok());
        var lu = image ?? Result<string?>.Ok("1");
        return (new ExecuteurJournalise(interne, journal, op => () => lu, _ => appelant ?? App,
            () => new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero)), interne, journal);
    }

    [Fact]
    public async Task L_image_avant_est_notee_sur_le_disque_avant_l_ecriture()
    {
        var (exec, interne, journal) = Monter();

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        interne.EntreesAuMomentDExecuter.Should().Be(1, "l'entrée existe déjà quand l'écriture part");
        interne.Executees.Should().Equal(Hags);
        journal.EnAttente().Value.Should().HaveCount(1);
        var e = journal.EnAttente().Value!.Single();
        e.Op.Should().Be(Hags);
        e.ImageAvant.Should().Be("1");
        e.Appelant.Should().Be(App);
    }

    [Fact]
    public async Task Une_image_avant_illisible_n_execute_rien()
    {
        var (exec, interne, journal) = Monter(image: Result<string?>.Fail("IO", "illisible"));

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        interne.Executees.Should().BeEmpty();
        journal.EnAttente().Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_appelant_qu_on_ne_sait_pas_identifier_n_execute_rien()
    {
        var journal = new JournalDEcritureAnticipee(Fichier);
        var interne = new Interne(journal, Result.Ok());
        var exec = new ExecuteurJournalise(interne, journal, op => () => Result<string?>.Ok("1"), _ => null, () => DateTimeOffset.UtcNow);

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        interne.Executees.Should().BeEmpty();
    }

    // Une lecture du registre, de NVAPI ou d'ADLX peut lever : ce qui ne se note pas ne s'exécute pas, sans exception.
    [Fact]
    public async Task Une_lecture_qui_leve_n_execute_rien_et_rend_un_echec()
    {
        var journal = new JournalDEcritureAnticipee(Fichier);
        var interne = new Interne(journal, Result.Ok());
        var exec = new ExecuteurJournalise(interne, journal, op => () => throw new System.Security.SecurityException("registre"),
            _ => App, () => DateTimeOffset.UtcNow);

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        interne.Executees.Should().BeEmpty();
        journal.EnAttente().Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Une_identification_qui_leve_n_execute_rien_et_rend_un_echec()
    {
        var journal = new JournalDEcritureAnticipee(Fichier);
        var interne = new Interne(journal, Result.Ok());
        var exec = new ExecuteurJournalise(interne, journal, op => () => Result<string?>.Ok("1"),
            _ => throw new InvalidOperationException("processus"), () => DateTimeOffset.UtcNow);

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        interne.Executees.Should().BeEmpty();
    }

    [Fact]
    public async Task Une_ecriture_qui_echoue_sort_du_journal()
    {
        var (exec, _, journal) = Monter(reponse: Result.Fail("HELPER", "refusé"));

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        journal.EnAttente().Value.Should().BeEmpty();
    }

    // Un échec qui laisse la cible changée (retour en arrière raté) : l'entrée reste, et le rejeu rendra l'origine.
    [Fact]
    public async Task Une_ecriture_qui_echoue_en_laissant_la_cible_changee_reste_au_journal()
    {
        var (exec, _, journal) = Monter(reponse: Result.Fail(ErrorCodes.TweakPartiallyApplied, "posé"));

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeFalse();

        journal.EnAttente().Value.Should().ContainSingle();
    }

    // Une pose marquée sans demandeur contournerait le journal : bloquée. Une op nue, elle, passe (un retour de bail).
    [Fact]
    public async Task Sans_appelant_une_pose_marquee_est_refusee_une_op_nue_passe()
    {
        var (exec, interne, _) = Monter();

        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None)).IsOk.Should().BeFalse();
        interne.Executees.Should().BeEmpty();

        (await exec.ExecuteAsync(Hags, CancellationToken.None)).IsOk.Should().BeTrue();
        interne.Executees.Should().Equal(Hags);
    }

    // Une pose marquée d'une op qui n'est pas journalisée garde le comportement actuel : ni journal, ni refus.
    [Fact]
    public async Task Une_pose_marquee_d_une_op_non_journalisee_passe_sans_journal()
    {
        var (exec, interne, journal) = Monter(image: Result<string?>.Fail("IO", "illisible"));
        var sousTension = new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2500);

        (await exec.ExecuteAsync(Marquee(sousTension), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        interne.Executees.Should().Equal(sousTension);
        journal.EnAttente().Value.Should().BeEmpty();
    }

    // L'invariant : une annulation est une op nue. Réussie, puis son demandeur meurt avant tout acquit : rien n'est au
    // journal, donc rien ne la rejouera, et le levier qu'elle a enlevé ne revient jamais.
    [Fact]
    public async Task Une_annulation_reussie_n_est_jamais_journalisee_donc_jamais_rejouee()
    {
        var (exec, interne, journal) = Monter();
        var annulation = Hags with { Value = "1" };

        (await exec.ExecuteAsync(annulation, CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        interne.Executees.Should().Equal(annulation);
        journal.EnAttente().Value.Should().BeEmpty();
    }

    // L'App enveloppe ses poses sans connaître les classes : l'acquit d'une pose qui n'a pas été journalisée réussit, sans
    // rien chercher au journal. Un acquit en échec reste ainsi le signal d'un vrai problème.
    [Fact]
    public async Task L_acquit_d_une_pose_non_journalisee_reussit_sans_toucher_au_journal()
    {
        var (exec, _, journal) = Monter();
        var sousTension = new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2500);
        (await exec.ExecuteAsync(Marquee(sousTension), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        (await exec.ExecuteAsync(Acquit(sousTension), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        journal.EnAttente().Value.Should().BeEmpty();
    }

    // L'acquit retire l'entrée de son appelant, et d'aucun autre : un processus ne peut pas effacer la protection d'un autre.
    [Fact]
    public async Task Un_appelant_n_acquitte_que_sa_propre_pose()
    {
        var journal = new JournalDEcritureAnticipee(Fichier);
        var interne = new Interne(journal, Result.Ok());
        AppelantDuJournal qui = App;
        var exec = new ExecuteurJournalise(interne, journal, op => () => Result<string?>.Ok("1"), _ => qui, () => DateTimeOffset.UtcNow);
        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        qui = Autre;
        (await exec.ExecuteAsync(Acquit(Hags), CancellationToken.None, new CallerInfo(5151))).IsOk.Should().BeFalse();
        journal.EnAttente().Value.Should().HaveCount(1);

        qui = App;
        (await exec.ExecuteAsync(Acquit(Hags), CancellationToken.None, Pair)).IsOk.Should().BeTrue();
        journal.EnAttente().Value.Should().BeEmpty();
        interne.Executees.Should().Equal(Hags);
    }

    // Un acquit arrive APRÈS l'écriture : son refus porte son propre code. Sous celui d'un refus d'avant l'écriture, l'écran
    // pourrait dire « rien n'a été écrit » d'une pose bien en place.
    [Fact]
    public async Task Un_acquit_refuse_porte_son_propre_code_un_refus_d_avant_l_ecriture_garde_le_sien()
    {
        var journal = new JournalDEcritureAnticipee(Fichier, attente: TimeSpan.FromMilliseconds(50));
        var interne = new Interne(journal, Result.Ok());
        AppelantDuJournal? qui = App;
        var exec = new ExecuteurJournalise(interne, journal, op => () => Result<string?>.Ok("1"), _ => qui, () => DateTimeOffset.UtcNow);
        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).IsOk.Should().BeTrue();

        (await exec.ExecuteAsync(Acquit(Hags), CancellationToken.None)).Error?.Code.Should().Be(ExecuteurJournalise.CodeAcquitRefuse);
        (await exec.ExecuteAsync(new AcquitterEcritureOp("pas une op"), CancellationToken.None, Pair)).Error?.Code
            .Should().Be(ExecuteurJournalise.CodeAcquitRefuse);
        using (new FileStream(Fichier + ".verrou", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            (await exec.ExecuteAsync(Acquit(Hags), CancellationToken.None, Pair)).Error?.Code.Should().Be(ExecuteurJournalise.CodeAcquitRefuse);
            (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).Error?.Code
                .Should().Be(JournalDEcritureAnticipee.CodeErreur, "journal occupé avant l'écriture");
        }
        qui = null;
        (await exec.ExecuteAsync(Acquit(Hags), CancellationToken.None, Pair)).Error?.Code.Should().Be(ExecuteurJournalise.CodeAcquitRefuse);
        (await exec.ExecuteAsync(Marquee(Hags), CancellationToken.None, Pair)).Error?.Code.Should().Be(ExecuteurJournalise.CodeRefus);
        interne.Executees.Should().Equal(Hags);
    }

    // Les deux entrées du helper passent l'appelant : le serveur du tube celui du pair, le client en processus le sien.
    private sealed class Espion : IPrivilegedOpExecutor
    {
        public CallerInfo? Recu { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => Task.FromResult(Result.Fail("TEST", "sans appelant"));
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct, CallerInfo appelant)
        {
            Recu = appelant;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class ToutAutoriser : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    [Fact]
    public async Task Le_serveur_du_tube_passe_l_appelant_a_l_executeur()
    {
        var cle = new byte[32];
        Random.Shared.NextBytes(cle);
        var espion = new Espion();
        var serveur = new TypedPrivilegedServer(cle, new ToutAutoriser(), new OpAllowlist(), espion);
        var json = PrivilegedOpCodec.Serialize(Hags).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        var requete = new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = TypedRequestHmac.Compute(cle, json, nonce) };

        (await serveur.ProcessTypedRequestAsync(requete, CancellationToken.None, new CallerInfo(4242))).Ok.Should().BeTrue();

        espion.Recu.Should().Be(new CallerInfo(4242));
    }

    [Fact]
    public async Task Le_client_en_processus_passe_son_propre_processus()
    {
        var espion = new Espion();

        (await new InProcessElevatedClient(new OpAllowlist(), espion).ExecuteAsync(Hags, CancellationToken.None)).IsOk.Should().BeTrue();

        espion.Recu.Should().Be(new CallerInfo(Environment.ProcessId));
    }

    // Le PID seul ne désigne pas un processus : un PID réutilisé n'a pas la même heure de création. L'heure est en UTC,
    // la même forme au journal et au rejeu.
    [Fact]
    public void Un_appelant_vivant_est_identifie_par_son_compte_et_son_heure_de_creation_en_UTC()
    {
        var a = IdentiteDesAppelants.De(new CallerInfo(Environment.ProcessId));

        a.Should().NotBeNull();
        a!.Sid.Should().Be(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        a.Pid.Should().Be(Environment.ProcessId);
        a.CreationDuProcessus.Offset.Should().Be(TimeSpan.Zero);
        a.CreationDuProcessus.Should().BeCloseTo(new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Un_appelant_sans_pid_ou_disparu_n_est_pas_identifie()
    {
        IdentiteDesAppelants.De(new CallerInfo(null)).Should().BeNull();
        IdentiteDesAppelants.De(new CallerInfo(int.MaxValue)).Should().BeNull();
    }
}
