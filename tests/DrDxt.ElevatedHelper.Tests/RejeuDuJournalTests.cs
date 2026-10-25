using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le rejeu du journal d'écriture anticipée : une écriture dont le demandeur est mort avant d'en noter l'origine est rendue,
/// avec les règles de la désinstallation (rendue seulement si la valeur posée est encore là). Un demandeur vivant n'est pas
/// touché. Une machine factice : un dictionnaire de feuilles, rien n'écrit sur la vraie.
/// </summary>
public sealed class RejeuDuJournalTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-rejeu-" + Guid.NewGuid().ToString("N"));
    private string Fichier => Path.Combine(_dossier, "journal.json");
    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } }

    private const string Cle = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private static readonly AppelantDuJournal Mort = new("S-1-5-21-1", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly AppelantDuJournal Vivant = new("S-1-5-21-1", 777, new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset Le = new(2026, 10, 18, 11, 0, 0, TimeSpan.Zero);

    private static SetRegistryValueOp Hags(string valeur)
        => new(RegistryHiveKind.LocalMachine, Cle, "HwSchMode", RegistryValueType.DWord, valeur);

    // Une feuille de la session, dans la liste blanche du helper : le rejeu ne rend qu'une origine qu'il sait réécrire.
    private const string CleSession = @"Software\Microsoft\GameBar";
    private static SetRegistryValueOp Session(string valeur)
        => new(RegistryHiveKind.CurrentUser, CleSession, "AllowAutoGameMode", RegistryValueType.DWord, valeur);

    /// <summary>La machine : une valeur par feuille ; absente = pas de clé dans le dictionnaire.</summary>
    private sealed class Machine : ILectureDesCibles, IElevatedClient
    {
        public readonly Dictionary<string, string?> Feuilles = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PrivilegedOp> Ecrites = [];
        public bool Refuse;

        private static string Feuille(PrivilegedOp op) => op switch
        {
            SetRegistryValueOp s => $"{s.Hive}\\{s.Path}\\{s.Name}",
            DeleteRegistryValueOp d => $"{d.Hive}\\{d.Path}\\{d.Name}",
            SetActivePowerPlanOp => "plan",
            _ => op.GetType().Name,
        };

        public Result<string?> Lire(PrivilegedOp op) => Result<string?>.Ok(Feuilles.GetValueOrDefault(Feuille(op)));
        public Result<bool> CibleExiste(PrivilegedOp op) => Result<bool>.Ok(true);

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (Refuse) return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refus"));
            Ecrites.Add(op);
            switch (op)
            {
                case SetRegistryValueOp s: Feuilles[Feuille(s)] = s.Value; break;
                case DeleteRegistryValueOp d: Feuilles.Remove(Feuille(d)); break;
                case SetActivePowerPlanOp p: Feuilles["plan"] = p.PlanGuid.ToString("D"); break;
            }
            return Task.FromResult(Result.Ok());
        }
    }

    private RejeuDuJournal Rejeu(Machine m, string? sidCourant = "S-1-5-21-1")
        => new(new JournalDEcritureAnticipee(Fichier), m, m, a => a == Vivant, () => sidCourant);

    /// <summary>La pose telle que le helper l'a faite : l'image d'avant notée, puis l'écriture.</summary>
    private void Poser(Machine m, PrivilegedOp op, string? avant, AppelantDuJournal qui, DateTimeOffset? le = null)
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(op, avant, qui, le ?? Le).IsOk.Should().BeTrue();
        m.ExecuteAsync(op, CancellationToken.None).Result.IsOk.Should().BeTrue();
        m.Ecrites.Clear();
    }

    private IReadOnlyList<EntreeDuJournal> EnAttente() => new JournalDEcritureAnticipee(Fichier).EnAttente().Value!;

    [Fact]
    public async Task Une_ecriture_dont_le_demandeur_est_mort_est_rendue_et_sort_du_journal()
    {
        var m = new Machine();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "1";
        Poser(m, Hags("2"), "1", Mort);

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        r.IsOk.Should().BeTrue();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("1");
        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        // La feuille rendue se nomme aussi sans dépendre de la langue : l'App y reconnaît un levier qu'elle croyait gardé.
        r.Value.Lignes[0].Cle.Should().Be(RejeuDuJournal.CleDe(Hags("2"), Mort));
        EnAttente().Should().HaveCount(0);
    }

    // Un réglage DRS posé au profil d'un jeu qui ne le possédait pas : son image d'avant n'a pas de valeur, seulement sa
    // provenance. Sans elle, l'entrée se disait « non rendable », sortait du journal, et le réglage restait dans le pilote.
    private const string Jeu = @"C:\Games\Call of Duty\cod.exe";
    private static ApplyNvApiDrsSettingOp DrsAuJeu(uint valeur) => new(0x1057EB71u, valeur, Jeu);

    [Fact]
    public async Task Un_reglage_drs_que_le_profil_du_jeu_ne_possedait_pas_lui_est_retire()
    {
        var m = new Machine();
        Poser(m, DrsAuJeu(1), Execution.LectureDesCiblesWin32.ImageHeritee + nameof(DrsValueOrigin.InheritedFromGlobalProfile), Mort);
        m.Feuilles[nameof(ApplyNvApiDrsSettingOp)] = "1"; // la pose de DrDXT est encore là

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().Equal(new DeleteNvApiDrsSettingOp(0x1057EB71u, Jeu, DrsValueOrigin.InheritedFromGlobalProfile, 0));
        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        EnAttente().Should().BeEmpty();
    }

    // Une provenance inconnue ne prouve aucun retour : le helper refuse la suppression, l'entrée se dit non rendable.
    [Fact]
    public void Une_provenance_inconnue_ou_illisible_ne_donne_aucune_suppression_rendable()
    {
        RejeuDuJournal.OrigineDe(DrsAuJeu(1), Execution.LectureDesCiblesWin32.ImageHeritee + "Nulle").Should().BeNull();
        var inconnue = RejeuDuJournal.OrigineDe(DrsAuJeu(1), Execution.LectureDesCiblesWin32.ImageHeritee + nameof(DrsValueOrigin.Unknown));
        new OpAllowlist().Validate(inconnue!).IsOk.Should().BeFalse();
    }

    [Fact]
    public async Task Une_valeur_absente_avant_l_ecriture_est_supprimee()
    {
        var m = new Machine();
        Poser(m, Hags("2"), null, Mort);

        await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().Equal(new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, Cle, "HwSchMode"));
        m.Feuilles.Should().NotContainKey($"LocalMachine\\{Cle}\\HwSchMode");
    }

    // Une origine que le helper refuserait de réécrire (HwSchMode à 0) restait au journal à vie, refusée à chaque rejeu.
    // Elle se dit non rendable une fois, et sort.
    [Fact]
    public async Task Une_origine_que_le_helper_refuserait_se_dit_non_rendable_et_sort_du_journal()
    {
        var m = new Machine();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "0";
        Poser(m, Hags("2"), "0", Mort);

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().HaveCount(0);
        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.ValeurPoseeInconnue);
        EnAttente().Should().HaveCount(0);
    }

    // L'App vit encore : elle notera l'origine ou rendra la pose elle-même.
    [Fact]
    public async Task Une_ecriture_dont_le_demandeur_vit_n_est_pas_touchee()
    {
        var m = new Machine();
        Poser(m, Hags("2"), "1", Vivant);

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().HaveCount(0);
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("2");
        r.Value!.Lignes.Should().HaveCount(0);
        EnAttente().Should().ContainSingle();
    }

    // Quelqu'un a changé la valeur depuis : son choix est respecté, et l'entrée sort (il n'y a plus rien à rendre).
    [Fact]
    public async Task Une_valeur_changee_depuis_est_respectee()
    {
        var m = new Machine();
        Poser(m, Hags("2"), "1", Mort);
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "3";

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().HaveCount(0);
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("3");
        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Respecte);
        EnAttente().Should().HaveCount(0);
    }

    // Sous SYSTEM, ou dans un helper élevé avec le compte d'un autre administrateur, HKCU est une autre ruche : y rendre
    // l'origine écrirait chez quelqu'un d'autre et laisserait le levier du joueur posé. L'entrée attend un helper de son compte.
    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-autre-admin")]
    public async Task Une_ecriture_de_la_session_attend_un_helper_du_meme_compte(string sidCourant)
    {
        var m = new Machine();
        Poser(m, Session("2"), "1", Mort);

        var r = await Rejeu(m, sidCourant).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().HaveCount(0);
        r.Value!.Lignes.Should().ContainSingle().Which.OrigineEnSuspens.Should().BeTrue();
        EnAttente().Should().ContainSingle();
    }

    [Fact]
    public async Task Une_ecriture_de_la_session_est_rendue_par_un_helper_du_meme_compte()
    {
        var m = new Machine();
        Poser(m, Session("2"), "1", Mort);

        var r = await Rejeu(m, Mort.Sid).ExecuterAsync(CancellationToken.None);

        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        m.Feuilles[$"CurrentUser\\{CleSession}\\AllowAutoGameMode"].Should().Be("1");
        EnAttente().Should().HaveCount(0);
    }

    // Deux comptes, deux ruches : leurs poses de la même clé HKCU ne se fondent pas. Celle du compte courant est rendue,
    // l'autre attend son compte.
    [Fact]
    public async Task Deux_comptes_ne_fondent_pas_leurs_poses_de_session()
    {
        var m = new Machine();
        var autre = Mort with { Sid = "S-1-5-21-2", Pid = 5151 };
        Poser(m, Session("2"), "1", Mort, Le);
        new JournalDEcritureAnticipee(Fichier).Ajouter(Session("3"), "0", autre, Le.AddMinutes(1)).IsOk.Should().BeTrue();

        var r = await Rejeu(m, Mort.Sid).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().Equal(Session("1"));
        r.Value!.Lignes.Should().HaveCount(2);
        EnAttente().Should().ContainSingle().Which.Appelant.Should().Be(autre);
    }

    [Fact]
    public void Le_compte_courant_se_lit()
        => RejeuDuJournal.CompteCourant().Should().StartWith("S-1-");

    [Fact]
    public async Task Une_ecriture_refusee_au_rejeu_reste_au_journal()
    {
        var m = new Machine();
        Poser(m, Hags("2"), "1", Mort);
        m.Refuse = true;

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.EchecDEcriture);
        EnAttente().Should().ContainSingle();
    }

    // Deux poses de la même feuille, l'App morte après chacune : la plus ancienne image est la vraie origine.
    [Fact]
    public async Task Deux_poses_de_la_meme_feuille_rendent_la_plus_ancienne_origine()
    {
        var m = new Machine();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "1";
        Poser(m, Hags("2"), "1", Mort, Le);
        Poser(m, Hags("3"), "2", Mort, Le.AddMinutes(1));

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("1");
        m.Ecrites.Should().ContainSingle();
        EnAttente().Should().HaveCount(0);
        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
    }

    // L'horloge a reculé entre les deux poses : l'ordre d'inscription au journal fait foi, pas l'heure murale. La première
    // inscrite porte la vraie origine, la dernière la valeur à comparer.
    [Fact]
    public async Task Une_horloge_reculee_entre_deux_poses_rend_l_origine_de_la_premiere_inscrite()
    {
        var m = new Machine();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "1";
        Poser(m, Hags("2"), "1", Mort, Le);
        Poser(m, Hags("3"), "2", Mort, Le.AddHours(-1));

        await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("1");
    }

    [Fact]
    public async Task Le_plan_d_alimentation_d_origine_est_reactive()
    {
        var m = new Machine();
        var equilibre = KnownPowerPlans.Balanced;
        var hautes = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        Poser(m, new SetActivePowerPlanOp(hautes), equilibre.ToString("D"), Mort);

        await Rejeu(m).ExecuterAsync(CancellationToken.None);

        m.Ecrites.Should().Equal(new SetActivePowerPlanOp(equilibre));
    }

    [Fact]
    public async Task Un_journal_illisible_ne_rend_rien_et_le_dit()
    {
        // Un journal de DrDXT (fichier de confiance) devenu illisible ; un fichier qui n'est pas de confiance est mis de côté.
        DrDxt.Reconciler.Manifest.TrustedManifestRoot.WriteTrustedFile(Fichier, System.Text.Encoding.UTF8.GetBytes("{ pas du json"))
            .IsOk.Should().BeTrue();
        var m = new Machine();

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        r.IsOk.Should().BeFalse();
        m.Ecrites.Should().HaveCount(0);
        File.ReadAllText(Fichier).Should().Be("{ pas du json");
    }

    // Un PID réutilisé par un autre processus n'a pas la même heure de création : la pose de l'App morte se rend.
    [Fact]
    public void Le_processus_courant_vit_et_un_pid_reutilise_est_mort()
    {
        var moi = IdentiteDesAppelants.De(new Ipc.CallerInfo(Environment.ProcessId));

        moi.Should().NotBeNull();
        RejeuDuJournal.ProcessusVivant(moi!).Should().BeTrue();
        RejeuDuJournal.ProcessusVivant(moi! with { CreationDuProcessus = moi.CreationDuProcessus.AddSeconds(-1) }).Should().BeFalse();
    }

    // L'App a posé, noté l'origine, mais son acquit n'est jamais arrivé (helper parti), puis elle est morte. L'App relancée,
    // du même compte et vivante, ne protège pas l'entrée de l'ancienne : c'est un autre processus. L'origine est rendue, le
    // sens sûr, plutôt qu'un fichier d'acquits du profil qui pourrait empêcher le rejeu.
    [Fact]
    public async Task L_acquit_jamais_remis_d_une_App_morte_laisse_rendre_l_origine_meme_App_relancee()
    {
        var m = new Machine();
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"] = "1";
        Poser(m, Hags("2"), "1", Mort);
        var relancee = Mort with { Pid = 777, CreationDuProcessus = Vivant.CreationDuProcessus };

        var r = await new RejeuDuJournal(new JournalDEcritureAnticipee(Fichier), m, m, a => a == relancee, () => Mort.Sid)
            .ExecuterAsync(CancellationToken.None);

        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        m.Feuilles[$"LocalMachine\\{Cle}\\HwSchMode"].Should().Be("1");
    }

    // DirectXUserGlobalSettings porte aussi Auto HDR, que le joueur règle lui-même. Le rejeu rendrait l'image d'avant entière
    // et effacerait un Auto HDR basculé depuis : seules les clés de DrDXT reprennent leur origine, comme à la désinstallation.
    [Fact]
    public async Task Le_rejeu_de_la_chaine_DirectX_garde_l_Auto_HDR_du_joueur()
    {
        var m = new Machine();
        var chaine = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, DirectXUserGlobalSettings.Chemin, DirectXUserGlobalSettings.Nom,
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;AutoHDREnable=0;VRROptimizeEnable=1");
        Poser(m, chaine, "SwapEffectUpgradeEnable=1;AutoHDREnable=0", Mort);
        m.Feuilles[$"{RegistryHiveKind.CurrentUser}\\{DirectXUserGlobalSettings.Chemin}\\{DirectXUserGlobalSettings.Nom}"]
            = "SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=1";

        var r = await Rejeu(m).ExecuterAsync(CancellationToken.None);

        r.Value!.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        m.Ecrites.OfType<SetRegistryValueOp>().Should().ContainSingle()
            .Which.Value.Should().Be("AutoHDREnable=1;SwapEffectUpgradeEnable=1", "le VRR de DrDXT part, l'Auto HDR du joueur reste");
        EnAttente().Should().BeEmpty();
    }
}
