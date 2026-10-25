using System.IO;
using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Les leviers de la session (mode Jeu, enregistrement en arrière-plan, accélération de la souris) s'écrivent dans l'App, pas
// par le helper : leur pose n'entrait dans aucun journal, et l'App tuée avant d'avoir noté l'origine laissait le réglage changé
// sans trace. Par le vrai montage du client (ElevatedClientFactory.Monter), avec un faux registre : la pose est notée avant
// d'écrire, acquittée à la fermeture de la portée, et rendue au lancement suivant si l'App est morte avant.
public sealed class JournalDuCompteTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-jea-compte-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private static readonly SetRegistryValueOp ModeJeu =
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord, "1");
    // Le compte de ce processus : le rejeu ne rend une valeur de la session que dans la ruche de son demandeur.
    private static readonly AppelantDuJournal Moi = new(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, 4242, new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Une ruche en mémoire : chaque écriture est vue avec l'état du journal à cet instant.</summary>
    private sealed class Ruche(Func<int>? entreesAuJournal = null) : IRegistryActions
    {
        public Dictionary<string, string?> Valeurs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<int> EntreesVuesAEcrire { get; } = [];

        public Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value)
        {
            EntreesVuesAEcrire.Add(entreesAuJournal?.Invoke() ?? -1);
            Valeurs[$@"{path}\{name}"] = value;
            return Result.Ok();
        }

        public Result DeleteValue(RegistryHiveKind hive, string path, string name)
        {
            Valeurs[$@"{path}\{name}"] = null;
            return Result.Ok();
        }
    }

    private sealed class Lecture(Ruche ruche) : ILectureDesCibles
    {
        public Result<string?> Lire(PrivilegedOp ecriture) => ecriture switch
        {
            SetRegistryValueOp s => Result<string?>.Ok(ruche.Valeurs.GetValueOrDefault($@"{s.Path}\{s.Name}")),
            DeleteRegistryValueOp d => Result<string?>.Ok(ruche.Valeurs.GetValueOrDefault($@"{d.Path}\{d.Name}")),
            _ => Result<string?>.Fail(ErrorCodes.HelperOpFailed, "non lu"),
        };

        public Result<bool> CibleExiste(PrivilegedOp ecriture) => Result<bool>.Ok(true);
    }

    private sealed class HelperRefuse : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "pas ici"));
        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => Task.FromResult(Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "pas ici"));
    }

    private JournalDEcritureAnticipee Journal() => JournalDEcritureAnticipee.DuCompte(Path.Combine(_dossier, "jea-session.json"));

    private (IElevatedClient Client, Ruche Ruche) Monter()
    {
        var journal = Journal();
        var ruche = new Ruche(() => journal.EnAttente().Value!.Count);
        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = "0";
        var lecture = new Lecture(ruche);
        return (ElevatedClientFactory.Monter(new HelperRefuse(), journal, ruche, op => () => lecture.Lire(op), () => Moi), ruche);
    }

    [Fact]
    public async Task Une_pose_de_la_session_est_notee_avant_d_ecrire_et_acquittee_a_la_fermeture()
    {
        var (client, ruche) = Monter();

        using (var portee = PorteeDePose.Ouvrir())
        {
            (await client.ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeTrue();
            ruche.EntreesVuesAEcrire.Should().Equal([1], "l'entrée est sur le disque avant l'écriture");
            Journal().EnAttente().Value.Should().ContainSingle().Which.ImageAvant.Should().Be("0");
            (await portee.FermerAsync()).IsOk.Should().BeTrue();
        }

        Journal().EnAttente().Value.Should().BeEmpty("l'origine est notée chez l'App : l'entrée sort");
    }

    [Fact]
    public async Task Hors_portee_de_pose_rien_n_est_journalise()
    {
        var (client, ruche) = Monter();

        (await client.ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeTrue();

        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"].Should().Be("1");
        ruche.EntreesVuesAEcrire.Should().HaveCount(1, "témoin : l'écriture a eu lieu");
        Journal().EnAttente().Value.Should().BeEmpty("une annulation ou un retour arrière part nu, jamais rejoué");
    }

    // L'App tuée entre l'écriture et la note de l'origine : l'App suivante la rend à son lancement, et le dit.
    [Fact]
    public async Task L_App_morte_avant_l_acquit_l_App_suivante_rend_l_origine()
    {
        var (client, ruche) = Monter();
        using (PorteeDePose.Ouvrir())
            (await client.ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeTrue();

        var texte = await RejeuDuJournalDuCompte.TexteAsync(Journal(), new Lecture(ruche), ruche, vivant: _ => false);

        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"].Should().Be("0");
        texte.Should().NotBeNull();
        Journal().EnAttente().Value.Should().BeEmpty();
    }

    // Changé par le joueur depuis la pose : respecté, rien n'est écrit.
    [Fact]
    public async Task Une_valeur_changee_depuis_est_respectee()
    {
        var (client, ruche) = Monter();
        using (PorteeDePose.Ouvrir())
            await client.ExecuteAsync(ModeJeu, CancellationToken.None);
        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = "2";

        await RejeuDuJournalDuCompte.TexteAsync(Journal(), new Lecture(ruche), ruche, vivant: _ => false);

        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"].Should().Be("2");
    }

    // Une cible qui ne se lit pas : rien n'est écrit sans image notée.
    [Fact]
    public async Task Sans_image_d_avant_rien_n_est_ecrit()
    {
        var journal = Journal();
        var ruche = new Ruche();
        var client = ElevatedClientFactory.Monter(new HelperRefuse(), journal, ruche,
            _ => () => Result<string?>.Fail(ErrorCodes.HelperOpFailed, "illisible"), () => Moi);

        using (PorteeDePose.Ouvrir())
            (await client.ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeFalse();

        ruche.EntreesVuesAEcrire.Should().BeEmpty();
    }

    // L'acquit refusé (journal occupé à la fermeture de la portée) est gardé et repris : resté refusé, le lancement suivant
    // rendrait l'origine d'un levier pourtant gardé.
    [Fact]
    public async Task Un_acquit_refuse_est_repris_quand_le_journal_se_libere()
    {
        var chemin = Path.Combine(_dossier, "jea-occupe.json");
        var journal = JournalDEcritureAnticipee.DuCompte(chemin, attente: TimeSpan.FromMilliseconds(100));
        var ruche = new Ruche();
        ruche.Valeurs[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = "0";
        var lecture = new Lecture(ruche);
        var client = ElevatedClientFactory.Monter(new HelperRefuse(), journal, ruche, op => () => lecture.Lire(op), () => Moi);
        var nom = @"Local\DrDXT-jea-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(chemin).ToUpperInvariant())))[..32];

        using (var portee = PorteeDePose.Ouvrir())
        {
            (await client.ExecuteAsync(ModeJeu, CancellationToken.None)).IsOk.Should().BeTrue();
            using var tenu = new ManualResetEventSlim();
            using var lache = new ManualResetEventSlim();
            var detenteur = new Thread(() =>
            {
                using var m = new Mutex(false, nom);
                m.WaitOne();
                tenu.Set();
                lache.Wait();
                m.ReleaseMutex();
            });
            detenteur.Start();
            tenu.Wait();
            (await portee.FermerAsync()).IsOk.Should().BeFalse("le journal est occupé");
            lache.Set();
            detenteur.Join();
        }
        DrDxt.ElevatedHelper.ClientSessionUtilisateur.UnAcquitLocalAttend.Should().BeTrue();
        journal.EnAttente().Value.Should().HaveCount(1, "témoin : l'entrée est encore là");

        DrDxt.ElevatedHelper.ClientSessionUtilisateur.ReprendreLesAcquitsLocaux().Should().BeTrue();

        journal.EnAttente().Value.Should().BeEmpty();
    }
}
