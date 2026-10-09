using DrDxt.Application.Network;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Network;

public class PlafondEnvoiPendantLeJeuTests
{
    private sealed class Helper : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = new();
        public bool Refuse { get; set; }
        public int LeveAuProchain { get; set; }
        public Func<PrivilegedOp, bool>? RefusePour { get; set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ops.Add(op);
            if (LeveAuProchain > 0) { LeveAuProchain--; throw new InvalidOperationException("tube ferme"); }
            var refuse = Refuse || (RefusePour?.Invoke(op) ?? false);
            return Task.FromResult(refuse ? Result.Fail(ErrorCodes.HelperOpFailed, "non") : Result.Ok());
        }
    }

    private static (PlafondEnvoiPendantLeJeu Mode, Helper H, Func<bool, Task> Tick) Monter()
    {
        var h = new Helper();
        var enJeu = false;
        var mode = new PlafondEnvoiPendantLeJeu(h, _ => enJeu, "cod.exe", 503_000_000);
        return (mode, h, async j => { enJeu = j; await mode.TickAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task Le_plafond_se_pose_au_lancement_du_jeu_et_se_retire_a_sa_fermeture()
    {
        var (mode, h, tick) = Monter();
        (await mode.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeTrue();

        await tick(false);
        h.Ops.OfType<ApplyUploadCapOp>().Should().BeEmpty("pas de jeu, pas de plafond");

        await tick(true);
        await tick(true);
        h.Ops.OfType<ApplyUploadCapOp>().Should().ContainSingle()
            .Which.Should().Be(new ApplyUploadCapOp("cod.exe", 503_000_000));
        mode.PlafondPose.Should().BeTrue();

        var menage = h.Ops.OfType<RemoveUploadCapOp>().Count();
        await tick(false);
        h.Ops.OfType<RemoveUploadCapOp>().Should().HaveCount(menage + 1);
        mode.PlafondPose.Should().BeFalse();
    }

    [Fact]
    public async Task Le_filet_est_un_bail_unique_dont_l_inverse_retire_le_plafond()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        await tick(true); await tick(false); await tick(true);

        var bails = h.Ops.OfType<EstablishLeaseOp>().ToList();
        bails.Should().ContainSingle("un bail par partie pourrait retirer le plafond de la partie suivante");
        PrivilegedOpCodec.Deserialize(bails[0].RevertOpJson).Value.Should().BeOfType<RemoveUploadCapOp>();
        h.Ops.OfType<RenewLeaseOp>().Should().HaveCount(3, "renouvele a chaque battement, jeu ou pas");
    }

    [Fact]
    public async Task Sans_bail_le_mode_ne_pose_jamais_de_plafond()
    {
        var (mode, h, tick) = Monter();
        h.Refuse = true;
        (await mode.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeFalse();
        h.Refuse = false;
        await tick(true);
        h.Ops.OfType<ApplyUploadCapOp>().Should().BeEmpty("un plafond sans filet survivrait a un plantage");
    }

    // 26/09/2026 : helper tue net en pleine partie (fin de tache, plantage) : ses baux meurent avec lui,
    // la politique reste en memoire et bride l'envoi de tout le PC jusqu'au redemarrage. A la relance,
    // le nouveau bail est accepte, rien n'est « pose » a sa connaissance, jeu ferme : personne ne
    // retirait l'orpheline.
    [Fact]
    public async Task Demarrer_le_mode_retire_une_politique_restee_d_un_helper_tue()
    {
        var (mode, h, tick) = Monter();
        (await mode.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeTrue();
        h.Ops.OfType<RemoveUploadCapOp>().Should().ContainSingle("l'etat reel est inconnu au demarrage");
        h.Ops.FindIndex(o => o is RemoveUploadCapOp).Should().BeGreaterThan(
            h.Ops.FindIndex(o => o is EstablishLeaseOp), "le filet d'abord, puis le menage");

        await tick(false);
        h.Ops.OfType<RemoveUploadCapOp>().Should().ContainSingle("jeu ferme et rien de pose : pas de retrait a chaque battement");
    }

    [Fact]
    public async Task Un_menage_refuse_au_demarrage_ne_bloque_pas_le_mode()
    {
        var (mode, h, tick) = Monter();
        h.RefusePour = op => op is RemoveUploadCapOp;
        (await mode.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeTrue("le bail tient ; le retrait est un menage");

        var (leve, hl, _) = Monter();
        hl.RefusePour = op => op is RemoveUploadCapOp ? throw new IOException("tube ferme") : false;
        (await leve.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeTrue("un menage qui leve ne casse pas le demarrage");
        h.RefusePour = null;
        await tick(true);
        mode.PlafondPose.Should().BeTrue();
    }

    // 26/09/2026 : deux chemins de retrait que des mutations laissaient verts. Point de depart commun :
    // une pose qui repond « echec » (delai depasse) alors que le helper l'a peut-etre executee. `_pose`
    // est faux, et l'etat reel inconnu : le retrait doit partir quand meme.

    [Fact]
    public async Task Jeu_ferme_et_etat_inconnu_le_retrait_part_meme_sans_pose_connue()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        h.RefusePour = op => op is ApplyUploadCapOp;   // la pose repond echec
        await tick(true);
        mode.PlafondPose.Should().BeFalse();

        var menage = h.Ops.OfType<RemoveUploadCapOp>().Count();
        h.RefusePour = op => op is RenewLeaseOp;       // helper relance : le bail est perdu
        await tick(false);
        h.Ops.OfType<RemoveUploadCapOp>().Should().HaveCount(menage + 1, "une politique peut etre restee sur le helper relance");
    }

    [Fact]
    public async Task Eteindre_le_mode_retire_meme_quand_aucune_pose_n_est_connue()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        h.RefusePour = op => op is ApplyUploadCapOp;
        await tick(true);
        await mode.ArreterAsync(CancellationToken.None);
        h.Ops.Last().Should().BeOfType<RemoveUploadCapOp>("le bail etait ouvert : l'etat reel n'est pas connu");
    }

    [Fact]
    public async Task Eteindre_le_mode_en_pleine_partie_retire_le_plafond_tout_de_suite()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        await tick(true);
        await mode.ArreterAsync(CancellationToken.None);
        h.Ops.Last().Should().BeOfType<RemoveUploadCapOp>();
        mode.PlafondPose.Should().BeFalse();
    }

    [Fact]
    public async Task Un_battement_qui_leve_ne_casse_pas_le_mode_le_suivant_pose_le_plafond()
    {
        // 25/09/2026 : la boucle de l'appli n'attrape que l'annulation ; une exception la tuait.
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        h.LeveAuProchain = 1;
        await tick(true);
        mode.PlafondPose.Should().BeFalse();
        await tick(true);
        mode.PlafondPose.Should().BeTrue();

        var detecteurQuiLeve = new PlafondEnvoiPendantLeJeu(h, _ => throw new InvalidOperationException("acces refuse"), "cod.exe", 503_000_000);
        await detecteurQuiLeve.DemarrerAsync(CancellationToken.None);
        await detecteurQuiLeve.Invoking(m => m.TickAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    /// <summary>Applique chaque operation a sa fin, comme le vrai helper ; la seconde pose attend.</summary>
    private sealed class PoseLente : IElevatedClient
    {
        public TaskCompletionSource Liberer { get; } = new();
        public bool PlafondEnPlace { get; private set; }
        private int _poses;
        public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is ApplyUploadCapOp)
            {
                if (++_poses > 1) await Liberer.Task;
                PlafondEnPlace = true;
            }
            else if (op is RemoveUploadCapOp) PlafondEnPlace = false;
            return Result.Ok();
        }
    }

    [Fact]
    public async Task Un_ajustement_en_vol_quand_le_jeu_se_ferme_ne_laisse_pas_de_plafond()
    {
        // 25/09/2026 : l'ajustement lisait « pose » et envoyait une pose ; pendant ce temps le battement
        // voyait le jeu ferme et retirait. La pose arrivait apres le retrait : plafond en place, mode
        // persuade du contraire, aucun battement ne le retirait plus.
        var h = new PoseLente();
        var enJeu = true;
        var mode = new PlafondEnvoiPendantLeJeu(h, _ => enJeu, "cod.exe", 503_000_000);
        (await mode.DemarrerAsync(CancellationToken.None)).IsOk.Should().BeTrue();
        await mode.TickAsync(CancellationToken.None);
        h.PlafondEnPlace.Should().BeTrue();

        var ajustement = mode.ChangerPlafondAsync(400_000_000, CancellationToken.None);
        enJeu = false;
        var battement = mode.TickAsync(CancellationToken.None);
        h.Liberer.SetResult();
        await Task.WhenAll(ajustement, battement);

        h.PlafondEnPlace.Should().BeFalse("le jeu est ferme : aucun plafond ne doit rester");
        mode.PlafondPose.Should().BeFalse();
    }

    private sealed class BailPerdu : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = new();
        public bool RefuseAussiLaReouverture { get; set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ops.Add(op);
            var refuse = op is RenewLeaseOp || (op is EstablishLeaseOp && RefuseAussiLaReouverture && Ops.OfType<EstablishLeaseOp>().Count() > 1);
            return Task.FromResult(refuse ? Result.Fail(ErrorCodes.HelperOpFailed, "bail inconnu") : Result.Ok());
        }
    }

    /// <summary>Le vrai helper : a l'expiration du bail, il execute l'inverse (retire le plafond).</summary>
    private sealed class BailQuiExpire : IElevatedClient
    {
        public bool Expire { get; set; }
        public bool PlafondEnPlace { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is RenewLeaseOp && Expire)
            {
                Expire = false;
                PlafondEnPlace = false; // l'inverse a tourne a l'expiration
                return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "bail inconnu"));
            }
            if (op is ApplyUploadCapOp) PlafondEnPlace = true;
            if (op is RemoveUploadCapOp) PlafondEnPlace = false;
            return Task.FromResult(Result.Ok());
        }
    }

    [Fact]
    public async Task Un_bail_expire_en_partie_repose_le_plafond_que_le_helper_a_retire()
    {
        // 25/09/2026 : a l'expiration, le helper retire le plafond (l'inverse du bail). Le mode rouvrait
        // le bail mais gardait « pose » : plus rien ne protegeait la partie, l'ecran disait « actif ».
        var h = new BailQuiExpire();
        var mode = new PlafondEnvoiPendantLeJeu(h, _ => true, "cod.exe", 503_000_000);
        await mode.DemarrerAsync(CancellationToken.None);
        await mode.TickAsync(CancellationToken.None);
        h.PlafondEnPlace.Should().BeTrue();

        h.Expire = true;
        await mode.TickAsync(CancellationToken.None);

        h.PlafondEnPlace.Should().BeTrue("le jeu tourne : le plafond retire a l'expiration est repose");
        mode.PlafondPose.Should().BeTrue();
    }

    [Fact]
    public async Task Un_bail_perdu_se_rouvre_et_sans_filet_aucun_plafond_ne_reste()
    {
        // 25/09/2026 : le resultat du renouvellement etait ignore.
        var h = new BailPerdu();
        var mode = new PlafondEnvoiPendantLeJeu(h, _ => true, "cod.exe", 503_000_000);
        await mode.DemarrerAsync(CancellationToken.None);
        await mode.TickAsync(CancellationToken.None);
        h.Ops.OfType<EstablishLeaseOp>().Should().HaveCount(2, "le renouvellement refuse rouvre le bail");
        mode.PlafondPose.Should().BeTrue("le bail rouvert, le filet est la");

        h.RefuseAussiLaReouverture = true;
        await mode.TickAsync(CancellationToken.None);
        mode.PlafondPose.Should().BeFalse("plus de filet : le plafond deja pose est retire");
        h.Ops.Last().Should().BeOfType<RemoveUploadCapOp>();
    }

    [Fact]
    public async Task Changer_le_plafond_en_partie_le_reapplique_hors_partie_le_garde_pour_la_pose()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);

        (await mode.ChangerPlafondAsync(400_000_000, CancellationToken.None)).IsOk.Should().BeTrue();
        h.Ops.OfType<ApplyUploadCapOp>().Should().BeEmpty("hors partie, rien n'est pose");
        await tick(true);
        h.Ops.OfType<ApplyUploadCapOp>().Single().CapBitsPerSecond.Should().Be(400_000_000);

        await mode.ChangerPlafondAsync(320_000_000, CancellationToken.None);
        h.Ops.OfType<ApplyUploadCapOp>().Last().CapBitsPerSecond.Should().Be(320_000_000, "en partie, reapplique tout de suite");
        mode.PlafondBps.Should().Be(320_000_000);

        h.Refuse = true;
        (await mode.ChangerPlafondAsync(256_000_000, CancellationToken.None)).IsOk.Should().BeFalse();
        mode.PlafondBps.Should().Be(320_000_000, "un refus garde la valeur en place");
    }

    [Fact]
    public async Task L_annulation_remonte_toujours()
    {
        var annule = new PlafondEnvoiPendantLeJeu(new AnnuleHelper(), _ => true, "cod.exe", 503_000_000);
        await annule.DemarrerAsync(CancellationToken.None);
        await annule.Invoking(m => m.TickAsync(CancellationToken.None)).Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class AnnuleHelper : IElevatedClient
    {
        private int _appels;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => ++_appels <= 2 ? Task.FromResult(Result.Ok()) : throw new OperationCanceledException();
    }

    [Fact]
    public async Task Un_retrait_refuse_laisse_le_plafond_note_comme_pose_pour_reessayer()
    {
        var (mode, h, tick) = Monter();
        await mode.DemarrerAsync(CancellationToken.None);
        await tick(true);
        h.Refuse = true;
        await tick(false);
        mode.PlafondPose.Should().BeTrue("un echec de retrait ne se lit pas comme un retrait");
        h.Refuse = false;
        await tick(false);
        mode.PlafondPose.Should().BeFalse();
    }

    // 26/09/2026 : le plafond ne se pose que si le jeu est vu vivant, et ce test tournait jusqu'ici sur une
    // doublure. Sur les vrais processus : le nom que le scan fournit (nom du processus + « .exe ») comme un
    // chemin complet retrouvent un processus qui tourne ; un nom inventé ne retrouve rien.
    [Fact]
    public void Le_jeu_est_reconnu_vivant_sur_les_vrais_processus()
    {
        using var moi = System.Diagnostics.Process.GetCurrentProcess();
        var exe = moi.ProcessName + ".exe";

        PlafondEnvoiPendantLeJeu.ProcessusVivant(exe).Should().BeTrue();
        PlafondEnvoiPendantLeJeu.ProcessusVivant(Path.Combine(@"C:\Jeux\Dossier", exe)).Should().BeTrue();
        PlafondEnvoiPendantLeJeu.ProcessusVivant(exe.ToUpperInvariant()).Should().BeTrue("Windows ignore la casse des noms");
        PlafondEnvoiPendantLeJeu.ProcessusVivant("aucun-jeu-" + Guid.NewGuid().ToString("N") + ".exe").Should().BeFalse();
    }
}
