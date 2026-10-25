using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// HagsOnTweak. Active la planification GPU accélérée par le matériel via la feuille
// ...\GraphicsDrivers\HwSchMode en liste blanche (DWORD 2 = activé). Capture d'abord la
// vraie origine → instantané inverse sérialisé ; redémarrage requis (preuve A/B différée).
// Tests unitaires purs avec des faux NSubstitute : aucun accès réel au registre.
// -----------------------------------------------------------------------
public sealed class HagsOnTweakTests
{
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HwSchModeName = "HwSchMode";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    /// <param name="hags">L'etat EFFECTIF (D3DKMT) — c'est lui que le gate lit.</param>
    /// <param name="cle">La cle `HwSchMode` ; par defaut, la meme chose que l'effectif.</param>
    private static SettingsState State(TriState hags, TriState? cle = null) => new SettingsState(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), cle ?? hags,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On)
        with { HagsEffective = hags };

    // --- La CLE ne decide plus rien, l'etat EFFECTIF decide. ---

    [Fact]
    public void Cle_ABSENTE_mais_effectif_Off_lu_est_un_candidat()
        // Le cas que l'ancien gate ratait : personne n'a ecrit HwSchMode, mais D3DKMT dit « eteint ».
        => Build().AppliesTo(Hw, State(TriState.Off, cle: TriState.Unknown)).Should().BeTrue();

    [Fact]
    public void Cle_a_Off_mais_effectif_ON_n_est_PAS_un_candidat()
        // Le placebo : ecrire 2 dans une cle pour activer un ordonnancement deja actif.
        => Build().AppliesTo(Hw, State(TriState.On, cle: TriState.Off)).Should().BeFalse();

    // HAGS ne dépend pas du constructeur : AppliesTo se base sur state.Hags, pas sur la carte.
    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.94", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.A0", null, true),
        ChassisType.Desktop);

    private HagsOnTweak Build()
    {
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName)
            .Returns(Result<uint?>.Ok(1u)); // actuellement désactivé (1)
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new HagsOnTweak(_registry, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.hagsOn");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.TakesEffectAfterReboot.Should().BeTrue(); // HwSchMode ne s'applique qu'après redémarrage
    }

    [Fact]
    public void SummaryKey_is_honest_and_never_claims_fps()
    {
        Build().SummaryKey.Should().Be("tweak.latency.hags.enable");
        Build().SummaryKey.Should().NotContain("fps");
    }

    [Fact]
    public void AppliesTo_seulement_quand_HAGS_est_LU_et_OFF()
    {
        var t = Build();
        t.AppliesTo(Hw, State(TriState.Off)).Should().BeTrue();
        t.AppliesTo(Hw, State(TriState.On)).Should().BeFalse("deja actif → rien a faire");
    }

    /// <summary>
    /// Ce test affirmait l'inverse : « HwSchMode absent =
    /// HAGS eteint → candidat offrable ». La cle absente ne dit rien de l'etat effectif — elle
    /// dit que personne n'a ecrit ce reglage, et Windows applique alors son propre defaut, qui
    /// depend de la version, du GPU et du pilote.
    ///
    /// Ecrire pour « corriger » un etat que personne n'a lu, puis mesurer l'ecart, revient a
    /// mesurer un changement dont le point de depart est inconnu. C'est exactement le cas de la
    /// machine de reference : la cle est absente.
    ///
    /// Consequence assumee : HAGS sort des candidats automatiques tant qu'aucune lecture de
    /// l'etat EFFECTIF n'existe. Unknown ne se corrige pas, il se lit.
    /// </summary>
    [Fact]
    public void Un_etat_INCONNU_n_est_JAMAIS_un_candidat_automatique()
    {
        Build().AppliesTo(Hw, State(TriState.Unknown)).Should().BeFalse(
            "un candidat fonde sur une absence de cle est un candidat fonde sur rien");
    }

    [Fact]
    public async Task Apply_captures_original_and_writes_hags_on_via_helper()
    {
        var t = Build(); // origine = 1 (désactivé)

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Écrit HwSchMode = 2 (activé) sur la feuille en liste blanche.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o =>
                o.Hive == RegistryHiveKind.LocalMachine
                && o.Path == GraphicsDriversPath
                && o.Name == HwSchModeName
                && o.Kind == RegistryValueType.DWord
                && o.Value == "2"),
            Arg.Any<CancellationToken>());
        // L'instantané porte l'origine capturée (1) en op inverse sérialisée.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetRegistryValueOp>().Which.Value.Should().Be("1");
    }

    [Fact]
    public async Task Apply_when_value_absent_captures_a_delete_inverse()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName)
            .Returns(Result<uint?>.Ok((uint?)null));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value
            .Should().BeOfType<DeleteRegistryValueOp>();
    }

    [Fact]
    public async Task Apply_fails_clean_when_registry_read_fails()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName)
            .Returns(Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "registry unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _registry.ReadDword(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName, RegistryValueType.DWord, "1");
        var snap = new TweakSnapshot("latency.hagsOn", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == HwSchModeName && o.Value == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.hagsOn", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
    // Banc profond : HAGS effectif, HwSchMode absent (le défaut du pilote, l'état de la machine de référence) ou à 2.
    // L'exercice écrit 1, le levier écrit 2, l'origine exacte revient (absente = supprimée), chaque étape relue dans le
    // registre ; HAGS n'agit qu'au redémarrage, et le rapport le dit.
    [Theory]
    [InlineData(null)]
    [InlineData(2u)]
    public async Task En_profond_HAGS_effectif_est_exerce_dans_le_registre_et_l_effet_au_redemarrage_est_dit(uint? origine)
    {
        var valeur = origine;
        var vues = new List<uint?>();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName).Returns(_ => Result<uint?>.Ok(valeur));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            valeur = c.Arg<PrivilegedOp>() switch { SetRegistryValueOp o => uint.Parse(o.Value), _ => null };
            vues.Add(valeur);
            return Task.FromResult(Result.Ok());
        });
        var levier = new HagsOnTweak(_registry, _elevated);
        var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(Path.Combine(Path.GetTempPath(), $"drdxt-profond-{Guid.NewGuid():N}.json"));
        DrDxt.Application.Autopilot.ValidationComplete.Lecture Relire(TweakSnapshot sn)
        {
            var p = DrDxt.Application.Tweaks.AppliedStateVerifier.Verifier(sn, _registry);
            return new(p.Verdict switch { DrDxt.Application.Tweaks.EtatApplique.Applied => true,
                DrDxt.Application.Tweaks.EtatApplique.NotApplied => false, _ => (bool?)null }, p.Verdict.ToString());
        }

        var r = await DrDxt.Application.Autopilot.ValidationComplete.ValiderAsync([levier], Hw, State(TriState.On), journal, Relire, profond: true);

        vues.Should().Equal(new uint?[] { 1, 2, origine });
        valeur.Should().Be(origine);
        r.Texte.Should().Contain("exercé").And.Contain("redémarrage");
        r.NonRemis.Should().BeEmpty();
    }

    // Le helper ne réécrit HwSchMode qu'à 1 ou 2 (OpAllowlist) : une origine à 0, ou à une valeur posée par un autre outil,
    // ne se remettrait jamais. Rien n'est écrit, et le refus se nomme.
    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    public async Task Une_origine_que_DrDXT_ne_remettrait_pas_n_est_pas_posee(uint origine)
    {
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName).Returns(Result<uint?>.Ok(origine));

        var r = await new HagsOnTweak(_registry, _elevated).ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.OrigineNonRendable);
        await _elevated.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task Une_origine_absente_ou_a_1_ou_2_se_pose(uint? origine)
    {
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName).Returns(Result<uint?>.Ok(origine));
        _elevated.ExecuteAsync(default!, default).ReturnsForAnyArgs(Result.Ok());

        (await new HagsOnTweak(_registry, _elevated).ApplyAsync()).IsOk.Should().BeTrue();
    }

    [Fact]
    public void HAGS_pas_effectif_ou_une_cle_a_1_n_est_pas_exerce()
    {
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName).Returns(Result<uint?>.Ok(1u));
        new HagsOnTweak(_registry, _elevated).PreparerExercice(Hw, State(TriState.On)).Should().BeNull("clé à 1 : un redémarrage en attente");
        _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName).Returns(Result<uint?>.Ok(2u));
        new HagsOnTweak(_registry, _elevated).PreparerExercice(Hw, State(TriState.Off)).Should().BeNull("pas effectif : pas déjà réglé");
    }
}
