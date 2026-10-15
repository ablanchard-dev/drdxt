using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Prouver qu'un levier est en place — et refuser de l'affirmer partout ailleurs.
///
/// Ce que ces tests figent, c'est une ASYMETRIE. Un seul verdict autorise a mesurer :
/// `Applied`. « Je ne sais pas lire », « la lecture a echoue », « le snapshot est ancien » ne
/// condamnent pas le levier — ils privent le produit du droit d'affirmer. La faute serait de
/// traiter l'un de ces trous comme une application reussie, puis de mesurer un ecart de session
/// et de l'attribuer a un changement dont personne n'a verifie l'existence.
/// </summary>
public class AppliedStateEvidenceTests
{
    private const string Chemin = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Feuille = "HwSchMode";

    private sealed class Lecteur(uint? valeur, bool echoue = false) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => echoue
                ? Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "lecture refusee")
                : Result<uint?>.Ok(valeur);

        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);

        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);
    }

    private static string Serialise(PrivilegedOp op)
    {
        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();
        return json.Value!;
    }

    /// <param name="avant">null = la valeur n'existait pas avant (l'inverse est une suppression).</param>
    private static TweakSnapshot Snapshot(string? avant, string demande)
    {
        PrivilegedOp inverse = avant is null
            ? new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, Feuille)
            : new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, Feuille,
                RegistryValueType.DWord, avant);

        var cible = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, Feuille,
            RegistryValueType.DWord, demande);

        return new TweakSnapshot("latency.hagsOn", Serialise(inverse), Serialise(cible));
    }

    [Fact]
    public void La_valeur_lue_EGALE_la_cible_donne_Applied()
    {
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: "1", demande: "2"), new Lecteur(2));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
        preuve.AutoriseLaMesure.Should().BeTrue("c'est le SEUL etat qui autorise un banc");
        preuve.RequestedState.Should().Be("2");
        preuve.ObservedState.Should().Be("2");
        preuve.Cible.Should().Contain(Feuille, "un constat doit etre verifiable a la main");
    }

    [Fact]
    public void La_valeur_restee_a_CELLE_D_AVANT_donne_NotApplied()
    {
        // Le cas qui fabrique un faux gain : l'helper rend Ok, la feuille n'a pas bouge (GPO,
        // pilote qui clampe, droits). Sans ce verdict, le moteur mesurait l'ecart d'une session
        // et l'attribuait a un changement inexistant.
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: "1", demande: "2"), new Lecteur(1));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    [Fact]
    public void Une_TROISIEME_valeur_donne_Drifted_et_pas_Applied()
    {
        // Quelqu'un d'autre ecrit sur cette feuille. Ce n'est ni notre cible, ni l'etat d'avant :
        // l'ancien readback aurait dit « different d'avant », ce qui ressemblait a un succes.
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: "1", demande: "2"), new Lecteur(7));

        preuve.Verdict.Should().Be(EtatApplique.Drifted);
        preuve.AutoriseLaMesure.Should().BeFalse();
        preuve.ObservedState.Should().Be("7");
    }

    [Fact]
    public void Une_valeur_ABSENTE_avant_et_ecrite_maintenant_donne_Applied()
    {
        // L'inverse est une suppression : « avant » = absente. La cible est bien en place.
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: null, demande: "2"), new Lecteur(2));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
        preuve.OriginalState.Should().BeNull();
    }

    [Fact]
    public void Une_valeur_ABSENTE_avant_et_TOUJOURS_absente_donne_NotApplied()
    {
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: null, demande: "2"), new Lecteur(null));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
    }

    [Fact]
    public void Un_snapshot_SANS_valeur_demandee_reste_Unknown()
    {
        // L'invariant qui compte : un snapshot ecrit avant l'existence de ce champ ne devient
        // pas « applique » par indulgence. C'est le cas de tous les manifestes deja sur disque.
        var ancien = new TweakSnapshot("latency.hagsOn",
            Serialise(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, Feuille,
                RegistryValueType.DWord, "1")));

        var preuve = AppliedStateVerifier.Verifier(ancien, new Lecteur(2));

        preuve.Verdict.Should().Be(EtatApplique.Unknown);
        preuve.AutoriseLaMesure.Should().BeFalse("inconnu n'est pas applique");
    }

    [Fact]
    public void Une_lecture_qui_ECHOUE_donne_Unknown_et_pas_NotApplied()
    {
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: "1", demande: "2"),
            new Lecteur(2, echoue: true));

        preuve.Verdict.Should().Be(EtatApplique.Unknown,
            "un trou de lecture n'accuse pas le levier, il interdit de conclure");
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    // ── Le plan d'alimentation se relit par `powrprof` ────────────────────────────────────

    private static readonly Guid Equilibre = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HautesPerfs = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private sealed class PlanFige(Guid? actif) : IPowerPlanReader
    {
        public Result<Guid> GetActivePowerPlan()
            => actif is { } g ? Result<Guid>.Ok(g)
                              : Result<Guid>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private static TweakSnapshot SnapshotPlan(Guid avant, Guid demande) =>
        new("fixit.power_plan",
            Serialise(new SetActivePowerPlanOp(avant)),
            Serialise(new SetActivePowerPlanOp(demande)));

    [Fact]
    public void Le_plan_demande_devenu_ACTIF_donne_Applied()
    {
        var preuve = AppliedStateVerifier.Verifier(
            SnapshotPlan(Equilibre, HautesPerfs), new Lecteur(null), null, new PlanFige(HautesPerfs));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
        preuve.AutoriseLaMesure.Should().BeTrue();
    }

    [Fact]
    public void Un_plan_reste_a_l_ancien_donne_NotApplied()
    {
        var preuve = AppliedStateVerifier.Verifier(
            SnapshotPlan(Equilibre, HautesPerfs), new Lecteur(null), null, new PlanFige(Equilibre));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
    }

    [Fact]
    public void Un_TROISIEME_plan_actif_donne_Drifted()
    {
        // Un autre outil, ou l'utilisateur, a bascule ailleurs entre-temps.
        var preuve = AppliedStateVerifier.Verifier(
            SnapshotPlan(Equilibre, HautesPerfs), new Lecteur(null), null,
            new PlanFige(Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61")));

        preuve.Verdict.Should().Be(EtatApplique.Drifted);
    }

    [Fact]
    public void Sans_lecteur_de_plan_c_est_Unavailable_et_pas_Applied()
    {
        // « ce lecteur ne sait pas » n'est pas « ce levier n'est pas verifiable ».
        var preuve = AppliedStateVerifier.Verifier(SnapshotPlan(Equilibre, HautesPerfs), new Lecteur(2));

        preuve.Verdict.Should().Be(EtatApplique.Unavailable);
    }

    [Fact]
    public void Un_ecart_de_FORMATAGE_ne_fait_pas_crier_a_l_echec()
    {
        // « 02 » et « 2 » sont la meme chose pour le registre. Comparer les chaines ferait
        // annoncer une application ratee qui n'existe pas.
        var preuve = AppliedStateVerifier.Verifier(Snapshot(avant: "1", demande: "02"), new Lecteur(2));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
    }

    // ── Les reglages du pilote NVIDIA se relisent par le pilote ────────────────────────────
    //
    // C'est ce qui fait passer `prefer_max_perf` de « on l'ecrit et on espere » a un candidat
    // dont l'application est PROUVABLE. La lecture existait deja (`GetPreferredPstate`) : le tweak
    // s'en servait pour capturer l'original, personne ne s'en servait pour verifier la cible.

    /// <param name="valeur">Ce que rend le profil de BASE.</param>
    /// <param name="auJeu">Ce que rend le profil du JEU ; null = non lisible a ce scope.</param>
    private sealed class NvApiFige(uint? valeur, bool disponible = true, uint? auJeu = null) : INvApi
    {
        public bool IsAvailable => disponible;
        public Result<(uint Value, DrsOriginState Origin)> GetDrsForApplication(uint settingId, string applicationPath)
            => auJeu is { } v
                ? Result<(uint, DrsOriginState)>.Ok((v, new DrsOriginState(DrsValueOrigin.SetByUser, true, "")))
                : Result<(uint, DrsOriginState)>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
        public Result<uint> GetPreferredPstate()
            => valeur is { } v ? Result<uint>.Ok(v)
                               : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
        public Result<uint> GetLowLatencyMode() => Result<uint>.Ok(0);
        public Result<uint> GetMaxPreRenderedFrames() => Result<uint>.Ok(0);
        public Result<uint> GetVsyncMode() => Result<uint>.Ok(0);
        public Result<uint> GetFrameRateLimit() => Result<uint>.Ok(0);
        public Result<DrsOriginState> GetFrameRateLimitOrigin()
            => Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu");
    }

    private const uint PreferredPstateId = 0x1057EB71;

    private static TweakSnapshot SnapshotDrs(uint avant, uint demande) =>
        new("latency.prefer_max_perf",
            Serialise(new ApplyNvApiDrsSettingOp(PreferredPstateId, avant, "Base Profile")),
            Serialise(new ApplyNvApiDrsSettingOp(PreferredPstateId, demande, "Base Profile")));

    [Fact]
    public void Un_reglage_pilote_applique_se_prouve_par_le_PILOTE()
    {
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrs(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(1));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
        preuve.AutoriseLaMesure.Should().BeTrue();
        preuve.Cible.Should().Contain("1057EB71", "la preuve doit nommer le reglage exact");
    }

    [Fact]
    public void Un_reglage_pilote_reste_a_sa_valeur_d_avant_donne_NotApplied()
    {
        // Le pilote peut refuser ou clamper : l'op « reussit » et rien ne bouge.
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrs(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(0));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
    }

    [Fact]
    public void Sans_pilote_disponible_le_reglage_est_Unavailable_et_pas_Applied()
    {
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrs(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(1, disponible: false));

        preuve.Verdict.Should().Be(EtatApplique.Unavailable);
        preuve.RequestedState.Should().Be("1", "la cible reste dite, meme quand on ne peut pas lire");
    }

    [Fact]
    public void Sans_NvApi_du_tout_le_reglage_est_Unavailable()
    {
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrs(avant: 0, demande: 1),
            new Lecteur(null));

        preuve.Verdict.Should().Be(EtatApplique.Unavailable);
    }

    [Fact]
    public void Une_lecture_pilote_qui_ECHOUE_donne_Unknown()
    {
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrs(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(null));

        preuve.Verdict.Should().Be(EtatApplique.Unknown);
    }

    // ── La relecture au meme scope que l'ecriture ────────────────────────────
    //
    // Le helper sait ecrire dans le profil d'UN jeu ; la relecture ne lisait que le profil de
    // base. Ces trois tests mettent la base et le jeu en DESACCORD : relire le mauvais scope les
    // fait tous rougir (mutation incluse par construction).

    private const string Jeu = @"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe";

    private static TweakSnapshot SnapshotDrsAuJeu(uint avant, uint demande) =>
        new("latency.prefer_max_perf",
            Serialise(new ApplyNvApiDrsSettingOp(PreferredPstateId, avant, Jeu)),
            Serialise(new ApplyNvApiDrsSettingOp(PreferredPstateId, demande, Jeu)));

    [Fact]
    public void Une_ecriture_au_profil_du_JEU_se_relit_au_jeu_et_pas_au_profil_de_base()
    {
        // La base porte deja la cible, le jeu est reste a l'original : l'ecriture n'a pas pris
        // la ou elle agit. Relire la base aurait prouve `Applied` et ouvert le banc sur un no-op.
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrsAuJeu(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(valeur: 1, auJeu: 0));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
        preuve.AutoriseLaMesure.Should().BeFalse();
        preuve.Cible.Should().Contain("cod.exe", "la preuve doit nommer le scope relu");
    }

    [Fact]
    public void Une_ecriture_au_profil_du_jeu_EN_PLACE_donne_Applied_meme_si_la_base_differe()
    {
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrsAuJeu(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(valeur: 0, auJeu: 1));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
    }

    [Fact]
    public void Sans_lecture_au_profil_du_jeu_c_est_Unknown_jamais_la_valeur_de_base()
    {
        // La base porte la cible ; le jeu ne se lit pas. Se replier sur la base serait affirmer.
        var preuve = AppliedStateVerifier.Verifier(SnapshotDrsAuJeu(avant: 0, demande: 1),
            new Lecteur(null), new NvApiFige(valeur: 1, auJeu: null));

        preuve.Verdict.Should().Be(EtatApplique.Unknown);
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    [Fact]
    public void Un_inverse_par_SUPPRESSION_porte_la_valeur_d_avant_et_donne_NotApplied()
    {
        // Quand le jeu HERITAIT la valeur, l'inverse est une suppression. Elle porte
        // la valeur d'avant ; sans elle, un reglage reste a 0 serait lu `Drifted`.
        var snapshot = new TweakSnapshot("latency.prefer_max_perf",
            Serialise(new DeleteNvApiDrsSettingOp(PreferredPstateId, Jeu,
                DrsValueOrigin.InheritedFromGlobalProfile, 0)),
            Serialise(new ApplyNvApiDrsSettingOp(PreferredPstateId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(null),
            new NvApiFige(valeur: 1, auJeu: 0));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
        preuve.OriginalState.Should().Be("0");
    }

    // ── Un levier composite se prouve feuille par feuille ──────
    //
    // Un snapshot composite (Vsync + plafond ; X3D = service + deux feuilles) ne se decodait pas
    // comme une op : verdict Unknown, jamais de banc, pour toujours.

    private const uint VsyncId = 0x00A879CF, FrlId = 0x10835002;

    private static string Composite(params PrivilegedOp[] ops) => CompositeSnapshotCodec.Encode(ops).Value!;

    private static SetRegistryValueOp Reg(string valeur) =>
        new(RegistryHiveKind.LocalMachine, Chemin, Feuille, RegistryValueType.DWord, valeur);

    [Fact]
    public void Un_composite_dont_TOUTES_les_feuilles_sont_en_place_donne_Applied()
    {
        var snapshot = new TweakSnapshot("latency.vrr_vsync_cap",
            Composite(new ApplyNvApiDrsSettingOp(FrlId, 0, Jeu), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu), new ApplyNvApiDrsSettingOp(FrlId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(null), new NvApiFige(valeur: 0, auJeu: 1));

        preuve.Verdict.Should().Be(EtatApplique.Applied);
        preuve.AutoriseLaMesure.Should().BeTrue();
        preuve.Cible.Should().Contain("A879CF").And.Contain("10835002", "la preuve nomme chaque feuille");
    }

    [Fact]
    public void Une_seule_feuille_restee_a_l_origine_suffit_a_refuser_le_banc()
    {
        // Feuille de registre en place (2), reglage du pilote reste a son origine (0).
        var snapshot = new TweakSnapshot("composite",
            Composite(Reg("1"), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(2), new NvApiFige(valeur: 0, auJeu: 0));

        preuve.Verdict.Should().Be(EtatApplique.Drifted);
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    [Fact]
    public void Les_feuilles_s_apparient_par_FEUILLE_pas_par_position()
    {
        // Inverses ranges a l'envers des ecritures, comme VrrVsyncCap. Apparies par position, chaque
        // feuille recevrait l'origine de l'autre et serait lue Drifted.
        var snapshot = new TweakSnapshot("composite",
            Composite(new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu), Reg("1")),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(1), new NvApiFige(valeur: 0, auJeu: 0));

        preuve.Verdict.Should().Be(EtatApplique.NotApplied);
    }

    [Fact]
    public void Une_feuille_que_personne_ne_sait_relire_rend_le_levier_Unavailable_jamais_Applied()
    {
        // Le levier X3D : un service + une feuille de registre. L'etat du service ne se relit pas ici.
        var snapshot = new TweakSnapshot("fixit.x3d_ccd",
            Composite(Reg("0"), new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Stop)),
            Composite(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start), Reg("1")));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(1));

        preuve.Verdict.Should().Be(EtatApplique.Unavailable);
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    // ── « UN TROU L'EMPORTE TOUJOURS » — la regle qui interdit d'affirmer ───────
    //
    // `Combiner` porte cette phrase dans son resume : « Un trou (Unknown, puis Unavailable)
    // l'emporte toujours : il interdit d'affirmer. » Elle decide ce que le produit a le droit
    // de dire d'un levier composite quand une seule de ses feuilles n'a pas pu etre lue.
    //
    // Mesure avant d'ecrire ces tests : la ligne `if (verdicts.Contains(Unknown)) return Unknown;`
    // retiree, la solution ENTIERE reste verte — 4 454 tests. La regle n'etait tenue par rien.
    //
    // Ce que sa disparition produirait : une feuille illisible melee a des feuilles en place
    // ressort `Drifted`. Le produit dirait « ca a derive » la ou il ne sait pas — et `Drifted`
    // autorise des gestes que `Unknown` interdit. Affirmer sur une lecture ratee est exactement
    // ce que ce dossier refuse partout ailleurs.

    [Fact]
    public void Une_feuille_ILLISIBLE_rend_le_composite_Unknown_et_pas_Drifted()
    {
        // Feuille DRS en place, feuille de registre dont la lecture echoue.
        var snapshot = new TweakSnapshot("composite",
            Composite(Reg("1"), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(null, echoue: true),
            new NvApiFige(valeur: 1, auJeu: 1));

        preuve.Verdict.Should().Be(EtatApplique.Unknown,
            "une lecture ratee n'est pas une derive : « je ne sais pas » ne se dit pas « ca a bouge »");
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    [Fact]
    public void Une_feuille_INDISPONIBLE_rend_le_composite_Unavailable_et_pas_Applied()
    {
        // Feuille de registre en place ; le pilote NVIDIA n'est pas la pour lire l'autre feuille.
        var snapshot = new TweakSnapshot("composite",
            Composite(Reg("1"), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(2), nvApi: null);

        preuve.Verdict.Should().Be(EtatApplique.Unavailable,
            "sans le pilote, la moitie du levier n'est pas lisible : le tout ne peut pas etre « applique »");
        preuve.AutoriseLaMesure.Should().BeFalse();
    }

    [Fact]
    public void Entre_deux_trous_c_est_le_plus_franc_qui_gagne()
    {
        // Les deux a la fois : lecture registre en echec et pilote absent. `Unknown` passe devant
        // `Unavailable` — « la lecture a echoue » est un fait plus dur que « je n'ai pas l'outil ».
        var snapshot = new TweakSnapshot("composite",
            Composite(Reg("1"), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(null, echoue: true), nvApi: null);

        preuve.Verdict.Should().Be(EtatApplique.Unknown);
    }

    [Fact]
    public void Un_trou_N_EFFACE_PAS_ce_qui_a_ete_lu()
    {
        // Le contrepoids : refuser d'affirmer ne veut pas dire se taire. La preuve doit encore
        // nommer les feuilles, sinon un verdict Unknown devient indiagnosticable pour le support.
        var snapshot = new TweakSnapshot("composite",
            Composite(Reg("1"), new ApplyNvApiDrsSettingOp(VsyncId, 0, Jeu)),
            Composite(Reg("2"), new ApplyNvApiDrsSettingOp(VsyncId, 1, Jeu)));

        var preuve = AppliedStateVerifier.Verifier(snapshot, new Lecteur(null, echoue: true),
            new NvApiFige(valeur: 1, auJeu: 1));

        preuve.Cible.Should().NotBeNullOrWhiteSpace();
        preuve.Cible.Should().Contain("+", "les deux feuilles restent nommees dans la preuve");
    }
}
