using DrDxt.Core;
using DrDxt.Reconciler.Reseau;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

/// <summary>La protection du ping portée par le service, sans DrDXT ouvert. À chaque battement :
/// le réglage retenu est relu, le plafond posé si le jeu tourne, retiré sinon. Pas de bail dans le service
/// (il n'y a pas de helper séparé qui pourrait mourir seul) : le filet, c'est le retrait au démarrage et à
/// l'arrêt, et les politiques QoS disparaissent de toute façon au redémarrage.</summary>
public sealed class ProtectionDuPingParLeServiceTests
{
    private sealed class ClientFaux : IElevatedClient
    {
        public List<PrivilegedOp> Ordres { get; } = new();
        public bool Refuser { get; set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ordres.Add(op);
            return Task.FromResult(Refuser ? Result.Fail(ErrorCodes.IoFailure, "refus") : Result.Ok());
        }
    }

    private readonly ClientFaux _client = new();
    private PlafondRetenuDuService? _retenu = new("cod.exe", 20_000_000);
    private readonly HashSet<string> _enCours = new(StringComparer.OrdinalIgnoreCase);

    private ProtectionDuPingParLeService Protection() => new(_client, () => _retenu, exe => _enCours.Contains(exe));

    [Fact]
    public async Task Au_demarrage_un_plafond_reste_d_avant_est_retire()
    {
        await Protection().DemarrerAsync(CancellationToken.None);

        _client.Ordres.Should().ContainSingle().Which.Should().BeOfType<RemoveUploadCapOp>();
    }

    // Un retrait refusé au démarrage se croyait fait : sans jeu lancé, aucun battement ne le retentait, et le plafond d'une vie
    // précédente du service bridait l'envoi de la machine jusqu'au redémarrage.
    [Fact]
    public async Task Un_retrait_refuse_au_demarrage_est_retente_au_battement_suivant()
    {
        var p = Protection();
        _retenu = null;
        _client.Refuser = true;
        await p.DemarrerAsync(CancellationToken.None);

        await p.TickAsync(CancellationToken.None);
        _client.Refuser = false;
        await p.TickAsync(CancellationToken.None);
        await p.TickAsync(CancellationToken.None);

        _client.Ordres.Should().HaveCount(3).And.AllBeOfType<RemoveUploadCapOp>("retenté jusqu'à ce qu'il passe, puis plus rien");
    }

    [Fact]
    public async Task Le_plafond_se_pose_au_lancement_du_jeu_et_se_retire_a_sa_fermeture()
    {
        var p = Protection();

        await p.TickAsync(CancellationToken.None);
        _client.Ordres.Should().BeEmpty("rien ne tourne : rien à poser, rien à retirer");

        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);
        await p.TickAsync(CancellationToken.None);
        _client.Ordres.Should().ContainSingle().Which.Should().Be(new ApplyUploadCapOp("cod.exe", 20_000_000),
            "posé une fois, pas à chaque battement");

        _enCours.Clear();
        await p.TickAsync(CancellationToken.None);
        _client.Ordres.Last().Should().BeOfType<RemoveUploadCapOp>();
        p.Pose.Should().BeNull();
    }

    [Fact]
    public async Task Le_joueur_eteint_la_protection_en_pleine_partie_le_plafond_part()
    {
        var p = Protection();
        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);

        _retenu = null;
        await p.TickAsync(CancellationToken.None);

        _client.Ordres.Last().Should().BeOfType<RemoveUploadCapOp>();
    }

    [Fact]
    public async Task Un_nouveau_plafond_retenu_est_repose_en_pleine_partie()
    {
        var p = Protection();
        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);

        _retenu = new("cod.exe", 15_000_000);
        await p.TickAsync(CancellationToken.None);

        _client.Ordres.Last().Should().Be(new ApplyUploadCapOp("cod.exe", 15_000_000));
    }

    [Fact]
    public async Task Une_pose_refusee_est_retentee_au_battement_suivant()
    {
        var p = Protection();
        _enCours.Add("cod.exe");
        _client.Refuser = true;
        await p.TickAsync(CancellationToken.None);
        p.Pose.Should().BeNull("un refus ne fait pas croire le plafond posé");

        _client.Refuser = false;
        await p.TickAsync(CancellationToken.None);

        p.Pose.Should().Be(new PlafondRetenuDuService("cod.exe", 20_000_000));
        _client.Ordres.Count(o => o is ApplyUploadCapOp).Should().Be(2);
    }

    [Fact]
    public async Task A_l_arret_du_service_le_plafond_pose_est_retire()
    {
        var p = Protection();
        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);

        await p.ArreterAsync(CancellationToken.None);

        _client.Ordres.Last().Should().BeOfType<RemoveUploadCapOp>();
        p.Pose.Should().BeNull();
    }

    private sealed class JournalFaux : IDiagnosticsLog
    {
        public List<(string Id, string De, string Vers)> Lignes { get; } = new();
        public void Change(string tweakId, string from, string to, string actor) => Lignes.Add((tweakId, from, to));
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(""));
    }

    // Une pose ou un retrait du service se voit après coup, comme ceux de l'App. Sans journal, la
    // protection portée par le service était invisible en vraie partie.
    [Fact]
    public async Task Chaque_pose_et_chaque_retrait_reussis_s_ecrivent_au_journal()
    {
        var journal = new JournalFaux();
        var p = new ProtectionDuPingParLeService(_client, () => _retenu, exe => _enCours.Contains(exe), journal);

        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);
        _enCours.Clear();
        await p.TickAsync(CancellationToken.None);

        journal.Lignes.Should().HaveCount(2);
        journal.Lignes[0].Vers.Should().Contain("20 Mbit/s").And.Contain("cod.exe");
        journal.Lignes[1].Vers.Should().Be("retiré");
    }

    // Windows qui s'éteint en pleine partie arrête le service : le retrait se faisait sans laisser de ligne, et
    // le journal finissait sur « posé ».
    [Fact]
    public async Task Le_retrait_a_l_arret_du_service_s_ecrit_aussi_au_journal()
    {
        var journal = new JournalFaux();
        var p = new ProtectionDuPingParLeService(_client, () => _retenu, exe => _enCours.Contains(exe), journal);
        _enCours.Add("cod.exe");
        await p.TickAsync(CancellationToken.None);

        await p.ArreterAsync(CancellationToken.None);
        await p.ArreterAsync(CancellationToken.None);

        journal.Lignes.Should().HaveCount(2, "un seul retrait écrit, rien quand rien n'était posé");
        journal.Lignes[1].Vers.Should().Be("retiré");
    }

    [Fact]
    public async Task Un_battement_qui_leve_ne_tue_pas_la_boucle()
    {
        var p = new ProtectionDuPingParLeService(_client, () => throw new IOException("disque"), _ => true);

        var battre = () => p.TickAsync(CancellationToken.None);

        await battre.Should().NotThrowAsync("l'état reste celui d'avant, le battement suivant réessaie");
    }
}
