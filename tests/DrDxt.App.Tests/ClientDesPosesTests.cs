using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Le client vers le helper marque les écritures journalisées d'une pose dont l'origine n'est pas encore notée, et les
/// acquitte à la fermeture de la portée. Hors portée, et pour toute op qui n'est pas journalisée, il ne change rien.
/// </summary>
public sealed class ClientDesPosesTests
{
    private sealed class Helper : IElevatedClient
    {
        public readonly List<PrivilegedOp> Recus = [];
        public Func<PrivilegedOp, Result> Repondre = _ => Result.Ok();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recus.Add(op);
            return Task.FromResult(Repondre(op));
        }
    }

    private static readonly SetActivePowerPlanOp Plan = new(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
    private static readonly SetCpuVoltageOffsetOp Tension = new(-50);

    // Un magasin d'acquits refusés par test : celui du processus est partagé entre les tests.
    private static ClientDesPoses Client(IElevatedClient helper, ClientDesPoses.AcquitsRefuses? refuses = null) => new(helper, refuses ?? new());

    private static PrivilegedOp Porte(string json) => PrivilegedOpCodec.Deserialize(json).Value!;

    [Fact]
    public async Task Hors_portee_tout_part_nu()
    {
        var helper = new Helper();

        (await Client(helper).ExecuteAsync(Plan, CancellationToken.None)).IsOk.Should().BeTrue();

        helper.Recus.Should().Equal(Plan);
    }

    [Fact]
    public async Task Dans_la_portee_seule_une_ecriture_journalisee_part_marquee()
    {
        var helper = new Helper();
        var client = Client(helper);
        PorteeDePose.Ouvrir();

        await client.ExecuteAsync(Plan, CancellationToken.None);
        await client.ExecuteAsync(Tension, CancellationToken.None);

        helper.Recus.Should().HaveCount(2);
        Porte(helper.Recus[0].Should().BeOfType<PoseEnAttenteDOrigineOp>().Subject.PoseJson).Should().Be(Plan);
        helper.Recus[1].Should().Be(Tension, "un réglage volatil n'a rien à rejouer");
    }

    // Le helper s'est tu après avoir reçu une pose marquée : si elle est posée, son entrée au journal la fera rendre au rejeu
    // qui suit la mort de l'App. Le code le porte, pour que l'écran le dise ; une op nue ne promet rien de tel.
    [Fact]
    public async Task Une_pose_marquee_sans_reponse_dit_que_le_rejeu_la_rendra()
    {
        var helper = new Helper { Repondre = _ => Result.Fail(ErrorCodes.HelperNoReply, "x") };
        PorteeDePose.Ouvrir();

        (await Client(helper).ExecuteAsync(Plan, CancellationToken.None)).Error?.Code.Should().Be(ErrorCodes.HelperNoReplyJournalisee);
        (await Client(helper).ExecuteAsync(Tension, CancellationToken.None)).Error?.Code.Should().Be(ErrorCodes.HelperNoReply,
            "un réglage volatil n'est pas au journal");
    }

    [Fact]
    public async Task Hors_portee_une_op_sans_reponse_ne_promet_aucun_rendu()
    {
        var helper = new Helper { Repondre = _ => Result.Fail(ErrorCodes.HelperNoReply, "x") };

        (await Client(helper).ExecuteAsync(Plan, CancellationToken.None)).Error?.Code.Should().Be(ErrorCodes.HelperNoReply);
    }

    [Fact]
    public async Task La_fermeture_acquitte_chaque_pose_marquee_une_fois_puis_tout_repart_nu()
    {
        var helper = new Helper();
        var client = Client(helper);
        var portee = PorteeDePose.Ouvrir();
        await client.ExecuteAsync(Plan, CancellationToken.None);
        await client.ExecuteAsync(Tension, CancellationToken.None);
        helper.Recus.Clear();

        (await portee.FermerAsync()).IsOk.Should().BeTrue();
        (await portee.FermerAsync()).IsOk.Should().BeTrue();

        Porte(helper.Recus.Should().ContainSingle().Which.Should().BeOfType<AcquitterEcritureOp>().Subject.PoseeJson).Should().Be(Plan);
        helper.Recus.Clear();
        await client.ExecuteAsync(Plan, CancellationToken.None);
        helper.Recus.Should().Equal(Plan);
    }

    [Fact]
    public async Task Une_pose_refusee_n_a_rien_a_acquitter()
    {
        var helper = new Helper { Repondre = _ => Result.Fail(ErrorCodes.HelperOpFailed, "refus") };
        var portee = PorteeDePose.Ouvrir();
        (await Client(helper).ExecuteAsync(Plan, CancellationToken.None)).IsOk.Should().BeFalse();
        helper.Recus.Clear();

        await portee.FermerAsync();

        helper.Recus.Should().BeEmpty();
    }

    // L'origine est notée chez l'App : la pose reste, l'acquit refusé se dit à la fermeture.
    [Fact]
    public async Task Un_acquit_refuse_est_rendu_par_la_fermeture()
    {
        var helper = new Helper();
        var portee = PorteeDePose.Ouvrir();
        await Client(helper).ExecuteAsync(Plan, CancellationToken.None);
        helper.Repondre = _ => Result.Fail(ErrorCodes.HelperOpFailed, "refus de l'acquit");

        var r = await portee.FermerAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Une_portee_suspendue_laisse_tout_partir_nu()
    {
        var helper = new Helper();
        PorteeDePose.Ouvrir();

        using (PorteeDePose.Suspendre())
            await Client(helper).ExecuteAsync(Plan, CancellationToken.None);

        helper.Recus.Should().Equal(Plan);
    }

    private static async Task<ClientDesPoses.AcquitsRefuses> UnAcquitRefuse()
    {
        var refuses = new ClientDesPoses.AcquitsRefuses();
        var parti = new Helper();
        var portee = PorteeDePose.Ouvrir();
        await Client(parti, refuses).ExecuteAsync(Plan, CancellationToken.None);
        parti.Repondre = _ => Result.Fail(ErrorCodes.HelperUnreachable, "helper parti");
        (await portee.FermerAsync()).IsOk.Should().BeFalse();
        return refuses;
    }

    // Le helper relancé ne connaît pas l'App, mais son journal garde l'entrée : l'acquit repart avant l'op suivante, une fois.
    [Fact]
    public async Task Un_acquit_refuse_repart_avant_l_op_suivante_vers_le_helper_relance()
    {
        var refuses = await UnAcquitRefuse();
        var relance = new Helper();
        var client = Client(relance, refuses);

        await client.ExecuteAsync(Tension, CancellationToken.None);
        await client.ExecuteAsync(Tension, CancellationToken.None);

        relance.Recus.Should().HaveCount(3);
        Porte(relance.Recus[0].Should().BeOfType<AcquitterEcritureOp>().Subject.PoseeJson).Should().Be(Plan);
        relance.Recus.Skip(1).Should().Equal(Tension, Tension);
    }

    [Fact]
    public async Task Le_battement_reprend_aussi_un_acquit_refuse()
    {
        var refuses = await UnAcquitRefuse();
        var relance = new Helper();

        await Client(relance, refuses).SansRelance.RenewLease("gpu-expert", 120, CancellationToken.None);

        relance.Recus.Should().HaveCount(2);
        relance.Recus[0].Should().BeOfType<AcquitterEcritureOp>();
        relance.Recus[1].Should().BeOfType<RenewLeaseOp>();
    }

    [Fact]
    public async Task A_la_fermeture_un_acquit_toujours_refuse_se_dit_au_joueur()
    {
        var refuses = await UnAcquitRefuse();
        var toujoursParti = new Helper { Repondre = _ => Result.Fail(ErrorCodes.HelperUnreachable, "helper parti") };

        (await Client(toujoursParti, refuses).DernierEssaiAsync()).Should().Be(Docteur.T("JEA.AcquitRefuse"));
        (await Client(new Helper(), refuses).DernierEssaiAsync()).Should().BeNull("l'acquit est enfin passé");
    }

    // Une entrée déjà rendue par un rejeu ne reviendra pas : reprendre son acquit coûterait un aller-retour par op, à vie.
    [Fact]
    public async Task Un_acquit_sans_entree_n_est_pas_repris()
    {
        var refuses = new ClientDesPoses.AcquitsRefuses();
        var helper = new Helper();
        var client = Client(helper, refuses);
        var portee = PorteeDePose.Ouvrir();
        await client.ExecuteAsync(Plan, CancellationToken.None);
        helper.Repondre = _ => Result.Fail(JournalDEcritureAnticipee.CodeSansEntree, "aucune entrée");
        await portee.FermerAsync();
        helper.Repondre = _ => Result.Ok();
        helper.Recus.Clear();

        await client.ExecuteAsync(Tension, CancellationToken.None);

        helper.Recus.Should().Equal(Tension);
        (await client.DernierEssaiAsync()).Should().BeNull();
    }

    // Reçu d'abord comme refus passager, puis « sans entrée » à la reprise : retiré aussi.
    [Fact]
    public async Task Un_acquit_repris_qui_n_a_plus_d_entree_est_retire()
    {
        var refuses = await UnAcquitRefuse();
        var relance = new Helper { Repondre = op => op is AcquitterEcritureOp ? Result.Fail(JournalDEcritureAnticipee.CodeSansEntree, "rendue") : Result.Ok() };
        var client = Client(relance, refuses);

        await client.ExecuteAsync(Tension, CancellationToken.None);
        await client.ExecuteAsync(Tension, CancellationToken.None);

        relance.Recus.Should().HaveCount(3);
        (await client.DernierEssaiAsync()).Should().BeNull();
    }

    private static readonly TimeSpan Borne = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_la_fermeture_un_acquit_passe_ne_montre_rien_et_un_refuse_se_montre()
    {
        (await ClientDesPoses.TexteDeFermetureAsync(() => Task.FromResult<string?>(null), () => false, Borne)).Should().BeNull();
        (await ClientDesPoses.TexteDeFermetureAsync(() => Task.FromResult<string?>("refusé"), () => false, Borne)).Should().Be("refusé");
    }

    // Un helper qui ne répond plus ne garde pas la fenêtre ouverte : passé la borne, l'acquit est tenu pour refusé.
    [Fact]
    public async Task Un_dernier_essai_qui_ne_finit_pas_est_borne_et_se_dit()
    {
        var jamais = new TaskCompletionSource<string?>();

        (await ClientDesPoses.TexteDeFermetureAsync(() => jamais.Task, () => false, TimeSpan.FromMilliseconds(50)))
            .Should().Be(Docteur.T("JEA.AcquitRefuse"));
    }

    // Une boîte bloquerait l'arrêt du PC : la session qui finit fait l'essai, sans rien montrer.
    [Fact]
    public async Task Quand_la_session_Windows_finit_rien_ne_se_montre()
    {
        var essais = 0;

        (await ClientDesPoses.TexteDeFermetureAsync(() => { essais++; return Task.FromResult<string?>("refusé"); }, () => true, Borne))
            .Should().BeNull();
        essais.Should().Be(1);
    }

    // Une pose qui échoue, ou une origine que l'App n'a pas pu noter : la portée tombe sans acquit, l'entrée reste au journal
    // du helper, qui rendra l'origine si l'App meurt.
    [Fact]
    public async Task Une_portee_abandonnee_n_acquitte_rien_et_rend_la_portee_d_avant()
    {
        var helper = new Helper();
        var avant = PorteeDePose.Ouvrir();
        var portee = PorteeDePose.Ouvrir();
        await Client(helper).ExecuteAsync(Plan, CancellationToken.None);
        helper.Recus.Clear();

        portee.Dispose();
        await portee.FermerAsync();

        helper.Recus.Should().BeEmpty();
        PorteeDePose.Courante.Should().BeSameAs(avant);
    }
}
