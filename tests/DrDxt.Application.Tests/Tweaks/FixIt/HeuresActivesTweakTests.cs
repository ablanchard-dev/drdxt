using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

/// <summary>
/// Le levier des heures d'activité. Il n'écrit que les deux heures du réglage utilisateur, par le
/// helper, capture l'original avant, et l'annulation le rend (absent ⇒ supprimé). Proposé seulement sur des parties lues
/// qui sortent des heures lues, jamais en mode automatique, jamais sur du non lu. Aucun faux helper n'écrit sur la machine.
/// </summary>
public sealed class HeuresActivesTweakTests
{
    private static readonly TimeSpan Ete = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("paris-ete", Ete, "paris-ete", "paris-ete");
    private static readonly PlageDeJeu Du0410 = new(new DateTimeOffset(2026, 10, 4, 8, 23, 48, Ete), new DateTimeOffset(2026, 10, 4, 8, 44, 3, Ete));

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();
    private readonly List<PrivilegedOp> _ecrits = [];

    private HeuresActivesTweak Build(uint? debut = 15, uint? fin = 8, uint? auto = 0, params PlageDeJeu[] parties)
    {
        _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut).Returns(Result<uint?>.Ok(debut));
        _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin).Returns(Result<uint?>.Ok(fin));
        _registry.ReadDword(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomAutomatique).Returns(Result<uint?>.Ok(auto));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        _elevated.When(e => e.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()))
            .Do(c => _ecrits.Add(c.Arg<PrivilegedOp>()));
        return new HeuresActivesTweak(_registry, _elevated, () => parties.Length == 0 ? [Du0410] : parties, Paris);
    }

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        [new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "610.88", TriState.On)],
        [new MemoryModule("PN", "G.Skill", 6000, 6000)],
        new MotherboardInfo("MSI", "B760"), new BiosInfo("1.0", null, true), ChassisType.Desktop);

    private static SettingsState Etat(HeuresActivesLues? heures, IReadOnlyList<PlageDeJeu>? parties) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: [], Vrr: TriState.On) { HeuresActives = heures, HeuresDeJeu = parties };

    private static SetRegistryValueOp Pose(string nom, string valeur) =>
        new(RegistryHiveKind.LocalMachine, HeuresActives.Cle, nom, RegistryValueType.DWord, valeur);

    [Fact]
    public void Propose_seulement_sur_des_parties_lues_hors_des_heures_lues_et_en_manuel()
    {
        var t = Build();
        t.AppliesTo(Hw, Etat(new(15, 8, null), [Du0410])).Should().BeFalse("F-17 : manuel non lu, Windows peut les régler seul");
        t.AppliesTo(Hw, Etat(new(15, 8, false), [Du0410])).Should().BeTrue();
        t.AppliesTo(Hw, Etat(new(15, 8, true), [Du0410])).Should().BeFalse("Windows les règle tout seul");
        t.AppliesTo(Hw, Etat(new(6, 15, false), [Du0410])).Should().BeFalse("la partie est déjà couverte");
        t.AppliesTo(Hw, Etat(new(15, 8, false), [])).Should().BeFalse("aucune partie relevée");
        t.AppliesTo(Hw, Etat(null, [Du0410])).Should().BeFalse();
        t.EtatNonLu(Etat(null, [Du0410])).Should().BeTrue();
        t.EtatNonLu(Etat(new(15, 8, null), [])).Should().BeFalse();
    }

    // Le lecteur d'état de la machine ne porte pas les parties (seul le scan de l'App les y ajoute) : la vérification de la
    // machine voyait « non lu ». Le levier lit alors les parties relevées lui-même, comme son Apply.
    [Fact]
    public void Sans_parties_dans_l_etat_le_levier_lit_les_parties_relevees()
    {
        var t = Build();

        t.EtatNonLu(Etat(new(15, 8, false), null)).Should().BeFalse();
        t.AppliesTo(Hw, Etat(new(15, 8, false), null)).Should().BeTrue("la partie relevée du 04/10 sort de 15 h à 8 h");
    }

    [Fact]
    public async Task Applique_les_deux_heures_par_le_helper_et_l_annulation_rend_l_original()
    {
        var t = Build();
        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        _ecrits.Should().Equal(Pose(HeuresActives.NomDebut, "15"), Pose(HeuresActives.NomFin, "9"));

        _ecrits.Clear();
        (await t.RevertAsync(r.Value!)).IsOk.Should().BeTrue();
        _ecrits.Should().Equal(Pose(HeuresActives.NomDebut, "15"), Pose(HeuresActives.NomFin, "8"));
    }

    // La seconde heure refusée, puis la première refusée à son tour au retour arrière : l'heure de début reste changée. Un
    // refus simple ferait lever le journal par l'appelant (« rien n'a changé ») ; le levier doit dire qu'il est à moitié posé.
    [Fact]
    public async Task Un_retour_arriere_refuse_est_dit_a_moitie_pose()
    {
        var t = Build();
        _elevated.ExecuteAsync(Pose(HeuresActives.NomFin, "9"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refus")));
        _elevated.ExecuteAsync(Pose(HeuresActives.NomDebut, "15"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()), Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refus du retour")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
    }

    // Un retour arrière journalisé serait rejoué après un arrêt sec et remettrait le levier : il part hors portée.
    [Fact]
    public async Task Le_retour_arriere_d_une_pose_part_hors_de_sa_portee()
    {
        var t = Build();
        var vues = new List<(PrivilegedOp Op, PorteeDePose? Portee)>();
        _elevated.When(e => e.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()))
            .Do(c => vues.Add((c.Arg<PrivilegedOp>(), PorteeDePose.Courante)));
        _elevated.ExecuteAsync(Pose(HeuresActives.NomFin, "9"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refus")));
        var portee = PorteeDePose.Ouvrir();

        await t.ApplyAsync();

        vues.Should().HaveCount(3);
        vues[0].Portee.Should().BeSameAs(portee, "la pose elle-même est dans la portée");
        vues[2].Op.Should().Be(Pose(HeuresActives.NomDebut, "15"));
        vues[2].Portee.Should().BeNull();
    }

    [Fact]
    public async Task Une_heure_absente_ne_se_regle_pas()
    {
        (await Build(debut: null, fin: 8).ApplyAsync()).IsOk.Should().BeFalse("une heure non lue ne se règle pas");
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public async Task Rien_n_est_ecrit_en_mode_automatique_ni_quand_les_parties_sont_deja_couvertes()
    {
        (await Build(auto: 1).ApplyAsync()).IsOk.Should().BeFalse();
        (await Build(debut: 6, fin: 15).ApplyAsync()).IsOk.Should().BeFalse();
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_echec_a_mi_chemin_rend_la_premiere_heure()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o.Equals(Pose(HeuresActives.NomFin, "9"))), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refusé")));

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        _ecrits.Should().EndWith(Pose(HeuresActives.NomDebut, "15"), "la première heure écrite est rendue");
    }

    // Le cas précédent garde le même début (15), un retour qui réécrirait la CIBLE au lieu
    // de l'original passait encore. Ici le début change : seul l'original (8) peut revenir.
    [Fact]
    public async Task Un_echec_a_mi_chemin_rend_l_original_quand_le_debut_change()
    {
        var nuit = new PlageDeJeu(new DateTimeOffset(2026, 10, 4, 19, 0, 0, Ete), new DateTimeOffset(2026, 10, 5, 3, 0, 0, Ete));
        var cible = HeuresActives.Proposer(new(8, 17, false), [nuit], Paris)!.Value;
        cible.Debut.Should().NotBe(8, "sinon ce test ne distingue rien");
        var t = Build(8, 17, 0, nuit);
        _elevated.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o.Equals(Pose(HeuresActives.NomFin, cible.Fin.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refusé")));

        (await t.ApplyAsync()).IsOk.Should().BeFalse();
        _ecrits.Should().EndWith(Pose(HeuresActives.NomDebut, "8"));
    }

    [Fact]
    public async Task Un_snapshot_illisible_ne_lance_rien()
    {
        (await Build().RevertAsync(new TweakSnapshot("fixit.heures_actives", "pas-du-json"))).IsOk.Should().BeFalse();
        _ecrits.Should().BeEmpty();
    }
    // Une lecture des parties relevées qui lève reste « non lu » : jamais une exception dans AppliesTo, donc au scan ou à la
    // vérification.
    [Fact]
    public void Une_lecture_des_parties_qui_leve_reste_non_lue()
    {
        var t = new HeuresActivesTweak(_registry, _elevated, () => throw new IOException("fichier verrouillé"), Paris);

        t.AppliesTo(Hw, Etat(new(15, 8, false), null)).Should().BeFalse();
        t.EtatNonLu(Etat(new(15, 8, false), null)).Should().BeTrue();
    }
}
