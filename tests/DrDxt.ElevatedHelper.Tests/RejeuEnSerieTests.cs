using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le rejeu relancé quand un client s'authentifie : l'App relancée après un plantage se connecte au helper resté vivant,
/// et ce que l'App morte a laissé est rendu ; ce qu'une App vivante a posé n'est pas touché.
/// </summary>
public sealed class RejeuEnSerieTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-rejeu-serie-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private const string Cle = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private static readonly AppelantDuJournal Morte = new("S-1-5-21-1", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly AppelantDuJournal Relancee = new("S-1-5-21-1", 5353, new DateTimeOffset(2026, 10, 18, 9, 5, 0, TimeSpan.Zero));

    private static SetRegistryValueOp Pose(string nom, string valeur) => new(RegistryHiveKind.LocalMachine, Cle, nom, RegistryValueType.DWord, valeur);

    /// <summary>La machine : chaque feuille porte ce que la pose y a mis ; une écriture la change.</summary>
    private sealed class Machine : ILectureDesCibles, IElevatedClient
    {
        public readonly Dictionary<string, string?> Feuilles = new(StringComparer.OrdinalIgnoreCase);
        public Result<string?> Lire(PrivilegedOp op) => Result<string?>.Ok(op is SetRegistryValueOp s ? Feuilles.GetValueOrDefault(s.Name) : null);
        public Result<bool> CibleExiste(PrivilegedOp op) => Result<bool>.Ok(true);
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is SetRegistryValueOp s) Feuilles[s.Name] = s.Value;
            return Task.FromResult(Result.Ok());
        }
    }

    [FactAdmin]
    public async Task Une_App_relancee_qui_se_connecte_fait_rendre_ce_que_la_morte_a_laisse()
    {
        var journal = new JournalDEcritureAnticipee(Path.Combine(_dossier, "journal.json"));
        var m = new Machine();
        m.Feuilles["HwSchMode"] = "2";
        m.Feuilles["TdrDelay"] = "8";
        journal.Ajouter(Pose("HwSchMode", "2"), "1", Morte, DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        journal.Ajouter(Pose("TdrDelay", "8"), "2", Relancee, DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        var rapports = new List<RapportDuRendu>();
        var rejeu = new RejeuEnSerie(ct => JournalDuHelper.RejouerAsync(journal, m, m, a => a == Relancee, () => Morte.Sid, ct),
            rapports.Add, TimeSpan.FromSeconds(10));

        await rejeu.Demander();

        m.Feuilles["HwSchMode"].Should().Be("1", "l'App morte ne notera jamais l'origine");
        m.Feuilles["TdrDelay"].Should().Be("8", "l'App vivante notera la sienne");
        journal.EnAttente().Value.Should().ContainSingle().Which.Appelant.Should().Be(Relancee);
        rapports.Should().ContainSingle();
    }

    // Une rafale de connexions pendant un rejeu n'en lance qu'un de plus, après lui.
    [Fact]
    public async Task Une_rafale_de_demandes_ne_lance_qu_un_rejeu_de_plus()
    {
        var libre = new TaskCompletionSource();
        var demarre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejeux = 0;
        var rejeu = new RejeuEnSerie(async _ =>
        {
            if (Interlocked.Increment(ref rejeux) == 1) demarre.SetResult();
            await libre.Task;
            return null;
        }, _ => { }, TimeSpan.FromSeconds(10));

        var premier = rejeu.Demander();
        // Le premier rejeu tourne : les demandes suivantes arrivent pendant lui, quelle que soit la charge de la machine.
        await demarre.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var suivantes = Enumerable.Range(0, 5).Select(_ => rejeu.Demander()).ToList();
        libre.SetResult();
        await premier;
        await Task.WhenAll(suivantes);

        rejeux.Should().Be(2);
    }

    [Fact]
    public async Task Un_rejeu_qui_leve_ne_leve_pas()
    {
        var rejeu = new RejeuEnSerie(_ => throw new InvalidOperationException("journal"), _ => { }, TimeSpan.FromSeconds(10));

        await rejeu.Invoking(r => r.RejouerAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public void Le_rapport_ecrit_se_relit()
    {
        Directory.CreateDirectory(_dossier);
        var le = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var rendu = new RapportDuRendu(null, [new LigneDuRendu(RejeuDuJournal.Source, "registre HwSchMode", IssueDuRendu.Rendu, "remis à 1")]);

        Result Ecrire(string chemin, byte[] octets) { File.WriteAllBytes(chemin, octets); return Result.Ok(); }
        for (var i = 0; i < RapportDuRejeu.Gardes + 2; i++)
            RapportDuRejeu.Ecrire(_dossier, RapportDuRejeu.Helper, rendu, le.AddMinutes(i), Ecrire, _ => true).IsOk.Should().BeTrue();

        var lus = RapportDuRejeu.Lire(_dossier, _ => true);
        lus.Should().HaveCount(RapportDuRejeu.Gardes, "seuls les derniers sont gardés");
        lus[^1].Le.Should().Be(le.AddMinutes(RapportDuRejeu.Gardes + 1));
        lus[^1].Lignes.Should().ContainSingle().Which.Should().Be(new RapportDuRejeu.Ligne("registre HwSchMode", IssueDuRendu.Rendu, "remis à 1", false));
        RapportDuRejeu.Lire(Path.Combine(_dossier, "absent"), _ => true).Should().HaveCount(0);
    }

    // %ProgramData% laisse tout compte y poser un fichier avant l'installation : un rapport hors confiance n'est ni dit à l'App
    // (de faux « rendu »), ni recopié dans le suivant.
    [Fact]
    public void Un_rapport_hors_confiance_n_est_ni_lu_ni_recopie()
    {
        Directory.CreateDirectory(_dossier);
        var le = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        RapportDuRendu Rendu(string detail) => new(null, [new LigneDuRendu(RejeuDuJournal.Source, "registre HwSchMode", IssueDuRendu.Rendu, detail)]);
        Result Ecrire(string chemin, byte[] octets) { File.WriteAllBytes(chemin, octets); return Result.Ok(); }
        RapportDuRejeu.Ecrire(_dossier, RapportDuRejeu.Helper, Rendu("posé d'avance"), le, Ecrire, _ => true).IsOk.Should().BeTrue();

        RapportDuRejeu.Lire(_dossier, _ => false).Should().BeEmpty();

        RapportDuRejeu.Ecrire(_dossier, RapportDuRejeu.Helper, Rendu("vrai"), le.AddMinutes(1), Ecrire, _ => false).IsOk.Should().BeTrue();
        RapportDuRejeu.Lire(_dossier, _ => true).SelectMany(r => r.Lignes).Select(l => l.Detail).Should().Equal("vrai");
    }

    // Le service rejoue à chaque démarrage et laisse en suspens la ruche du joueur : ce rapport-là n'efface pas celui du helper.
    [Fact]
    public void Un_rapport_tout_en_suspens_n_efface_pas_le_dernier()
    {
        var ecrits = 0;
        var suspens = new RapportDuRendu(null, [new LigneDuRendu(RejeuDuJournal.Source, "HKCU", IssueDuRendu.Illisible, "session", EnSuspens: true)]);

        RapportDuRejeu.Ecrire(_dossier, RapportDuRejeu.Service, suspens, DateTimeOffset.UtcNow, (_, _) => { ecrits++; return Result.Ok(); }, _ => true).IsOk.Should().BeTrue();

        ecrits.Should().Be(0);
    }

    // Une connexion en pleine partie ne lance pas un rejeu (bcdedit, NVAPI, écritures) : il attend la connexion suivante
    // hors jeu.
    [Fact]
    public async Task Le_rejeu_d_une_connexion_attend_la_fin_de_la_partie()
    {
        var rejeux = 0;
        var rejeu = new RejeuEnSerie(_ => { Interlocked.Increment(ref rejeux); return Task.FromResult<RapportDuRendu?>(null); }, _ => { },
            TimeSpan.FromSeconds(10));

        await rejeu.DemanderHorsJeu(() => true);
        rejeux.Should().Be(0, "un jeu tourne");

        await rejeu.DemanderHorsJeu(() => false);
        rejeux.Should().Be(1, "la connexion suivante, hors jeu, le lance");
    }

    [Fact]
    public void Le_helper_differe_le_rejeu_d_une_connexion_en_partie()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDxt.sln"))) d = d.Parent;
        var programme = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ElevatedHelper", "Program.cs"));

        programme.Should().Contain("_ = rejeu.DemanderHorsJeu(RejeuEnSerie.JeuConnuLance);");
    }
}
