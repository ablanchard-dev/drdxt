using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le journal d'écriture anticipée tel que les hôtes le construisent : sa racine réparée d'abord, l'exécuteur décoré ensuite,
/// le chien de garde des baux sur l'exécuteur intérieur.
/// </summary>
public sealed class JournalDuHelperTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-jea-hote-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_racine, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static readonly AppelantDuJournal App = new("S-1-5-21-1", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly SetRegistryValueOp Hags = new(RegistryHiveKind.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2");

    // Un dossier qu'un compte aurait créé d'avance, verrou tenu, rendrait le journal occupé pour toujours : la racine est
    // réparée avant que le dossier du journal n'existe.
    [Fact]
    public void La_racine_est_reparee_avant_que_le_dossier_du_journal_n_existe()
    {
        var vus = new List<bool>();

        var journal = JournalDuHelper.Ouvrir(_racine, racine =>
        {
            vus.Add(Directory.Exists(Path.Combine(racine, JournalDuHelper.Dossier)));
            Directory.CreateDirectory(racine);
            return Result.Ok();
        });

        vus.Should().Equal(false);
        journal.Should().NotBeNull();
        journal!.Ajouter(Hags, "1", App, DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        Directory.Exists(Path.Combine(_racine, JournalDuHelper.Dossier)).Should().BeTrue();
    }

    [Fact]
    public void Une_racine_que_la_reparation_refuse_ne_donne_pas_de_journal()
    {
        JournalDuHelper.Ouvrir(_racine, _ => Result.Fail(ErrorCodes.IoFailure, "racine squattée")).Should().BeNull();

        Directory.Exists(Path.Combine(_racine, JournalDuHelper.Dossier)).Should().BeFalse();
    }

    // Sans journal, l'exécuteur reste l'intérieur, qui refuse une pose marquée (PoseMarqueeTests).
    [Fact]
    public void Sans_journal_l_executeur_reste_l_interieur()
    {
        var interne = Substitute.For<IPrivilegedOpExecutor>();

        JournalDuHelper.Decorer(interne, null, _ => null).Should().BeSameAs(interne);
        JournalDuHelper.Decorer(interne, new JournalDEcritureAnticipee(Path.Combine(_racine, "j.json")), _ => null)
            .Should().BeOfType<ExecuteurJournalise>();
    }

    // Le retour d'un bail expiré est une annulation : il part nu, jamais journalisé, que le chien de garde tienne l'exécuteur
    // intérieur ou le décoré.
    [Fact]
    public async Task Un_bail_expire_rend_son_retour_sans_rien_journaliser()
    {
        var maintenant = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var horloge = Substitute.For<IClock>();
        horloge.UtcNow.Returns(_ => maintenant);
        var interne = Substitute.For<IPrivilegedOpExecutor>();
        interne.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        var journal = new JournalDEcritureAnticipee(Path.Combine(_racine, "j.json"));
        var decore = JournalDuHelper.Decorer(interne, journal, _ => () => Result<string?>.Ok("90"));
        var retour = new ApplyNvApiPowerLimitOp(0, 90);
        var w = new LeaseWatchdog(horloge, new OpAllowlist(), decore, Substitute.For<IDiagnosticsLog>());

        w.Establish(new LeaseOwner(ProcessId: 1), "gpu-limite", PrivilegedOpCodec.Serialize(retour).Value!, 1).IsOk.Should().BeTrue();
        maintenant = maintenant.AddSeconds(5);
        await w.TickAsync(CancellationToken.None);

        await interne.Received(1).ExecuteAsync(retour, Arg.Any<CancellationToken>());
        journal.EnAttente().Value.Should().HaveCount(0);
    }

    // Le rejeu au démarrage passe par la liste blanche, comme le tube.
    [Fact]
    public async Task Le_rejeu_au_demarrage_ecrit_par_la_liste_blanche()
    {
        var journal = new JournalDEcritureAnticipee(Path.Combine(_racine, "j.json"));
        journal.Ajouter(Hags, "1", App, DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        var lecture = Substitute.For<DrDxt.Reconciler.Desinstallation.ILectureDesCibles>();
        // Posée (2) au jugement, rendue (1) à la relecture qui suit l'écriture.
        lecture.Lire(Arg.Any<PrivilegedOp>()).Returns(Result<string?>.Ok("2"), Result<string?>.Ok("1"));
        lecture.CibleExiste(Arg.Any<PrivilegedOp>()).Returns(Result<bool>.Ok(true));
        var interne = Substitute.For<IPrivilegedOpExecutor>();
        interne.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        interne.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>(), Arg.Any<Ipc.CallerInfo>()).Returns(Result.Ok());

        var client = new DrDxt.ElevatedHelper.Execution.InProcessElevatedClient(new OpAllowlist(), interne);
        var rapport = await JournalDuHelper.RejouerAsync(journal, lecture, client, _ => false, () => App.Sid, CancellationToken.None);

        rapport!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(DrDxt.Reconciler.Desinstallation.IssueDuRendu.Rendu);
        await interne.Received(1).ExecuteAsync(Hags with { Value = "1" }, Arg.Any<CancellationToken>(), Arg.Any<Ipc.CallerInfo>());
        (await JournalDuHelper.RejouerAsync(null, lecture, client, _ => false, () => App.Sid, CancellationToken.None)).Should().BeNull();
    }

    private static string Source(string relatif)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDxt.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, relatif));
    }

    // Chaque hôte qui construit un exécuteur le décore : une pose marquée qui arrive par lui est journalisée.
    [Theory]
    [InlineData("src/DrDxt.ElevatedHelper/Program.cs")]
    [InlineData("src/DrDxt.ElevatedHelper/ModeDesinstallation.cs")]
    [InlineData("src/DrDxt.ReconcilerHost/HostServiceCollectionExtensions.cs")]
    public void Chaque_point_de_construction_decore_son_executeur(string fichier)
    {
        var texte = Source(fichier);

        System.Text.RegularExpressions.Regex.Matches(texte, @"new PrivilegedOpExecutor\(").Should().HaveCount(1);
        texte.Should().Contain("JournalDuHelper.Decorer(");
    }

    // Le tube sert l'exécuteur décoré ; le chien de garde garde l'intérieur.
    [Fact]
    public void Le_helper_sert_le_decore_et_garde_ses_baux_sur_l_interieur()
    {
        var texte = Source("src/DrDxt.ElevatedHelper/Program.cs");

        texte.Should().MatchRegex(@"new LeaseWatchdog\(\s*new SystemClock\(\), new OpAllowlist\(\), executor,");
        texte.Should().Contain("new OpAllowlist(), servi, pipeName, watchdog)");
        texte.Should().NotContain("new OpAllowlist(), executor, pipeName, watchdog)");
    }

    // L'App qui vient de lancer le helper n'attend le tube que quelques secondes : le rejeu ne passe jamais avant d'écouter.
    [Fact]
    public void Le_helper_ne_rejoue_jamais_en_attendant_avant_d_ecouter()
    {
        var texte = Source("src/DrDxt.ElevatedHelper/Program.cs");

        texte.Should().NotContain("await JournalDuHelper.RejouerAsync");
        texte.Should().NotContain("await rejeu.");
        // Lancé à la demande, le helper démarre souvent sur un geste de l'App, et ce geste peut tomber en pleine partie.
        System.Text.RegularExpressions.Regex.Matches(texte, @"_ = rejeu\.Demander\(\);").Should().HaveCount(0);
        System.Text.RegularExpressions.Regex.Matches(texte, @"_ = rejeu\.DemanderHorsJeu\(RejeuEnSerie\.JeuConnuLance\);").Should().HaveCount(2,
            "au démarrage du helper et après chaque authentification");
    }
}
