using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// VrrFlipModelOnTweak. Active VRR + modèle de retournement en fusionnant la REG_SZ
// partagée DirectXUserGlobalSettings (AutoHDR et autres préservés). La logique de fusion
// s'exerce ici par la surface publique du levier (la chaîne écrite est vérifiée).
// Redémarrage requis (preuve A/B différée). Faux NSubstitute, aucun vrai registre.
// -----------------------------------------------------------------------
public sealed class VrrFlipModelOnTweakTests
{
    private const string PrefsPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ValueName = "DirectXUserGlobalSettings";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.Off);

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.94", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.A0", null, true),
        ChassisType.Desktop);

    private VrrFlipModelOnTweak Build(string? original)
    {
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Ok(original));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // Windows 11 fixé : le build lu de la machine ferait changer la moitié flip-model sous Windows 10.
        return new VrrFlipModelOnTweak(_registry, _elevated, () => 22631);
    }

    // « Optimisations pour les jeux en fenêtré » n'existe que sous Windows 11 (support Microsoft). Sous Windows 10, la moitié
    // flip-model ne compte pas : seul le VRR fenêtré, sur un écran lu compatible, fait proposer le levier.
    [Fact]
    public void Sous_Windows_10_la_moitie_flip_model_ne_fait_pas_proposer_le_levier()
    {
        Build(null);
        var w10 = new VrrFlipModelOnTweak(_registry, _elevated, () => 19045);
        var w11 = new VrrFlipModelOnTweak(_registry, _elevated, () => 22631);

        w10.AppliesTo(Hw, State with { VrrCapableDisplay = TriState.Off }).Should().BeFalse();
        w11.AppliesTo(Hw, State with { VrrCapableDisplay = TriState.Off }).Should().BeTrue("témoin : Windows 11");
        w10.AppliesTo(Hw, State with { VrrCapableDisplay = TriState.On }).Should().BeTrue("la moitié VRR vaut sous Windows 10");
        w10.EtatNonLu(State with { VrrCapableDisplay = TriState.Unknown }).Should().BeTrue();
    }

    private async Task<string> CapturedWrittenValue(VrrFlipModelOnTweak t)
    {
        var r = await t.ApplyAsync();
        r.IsOk.Should().BeTrue();
        // Inspecter les appels enregistrés : l'unique SetRegistryValueOp sur notre valeur porte la fusion.
        var write = _elevated.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<SetRegistryValueOp>()
            .Single(o => o.Name == ValueName);
        return write.Value;
    }

    // Flip-model posé, moitié VRR absente, capacité VRR de l'écran jamais lue : rien ne dit que c'est réglé. La vérification
    // écrivait « déjà réglé » ; c'est « état non lu ». Un écran lu sans VRR, ou les deux moitiés posées, ne sont pas du non lu.
    [Fact]
    public void Un_ecran_dont_la_capacite_VRR_n_est_pas_lue_est_dit_non_lu()
    {
        var t = Build("SwapEffectUpgradeEnable=1;");

        t.AppliesTo(Hw, State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
        t.EtatNonLu(State with { VrrCapableDisplay = TriState.Off }).Should().BeFalse("lu : l'écran ne fait pas de VRR");
        Build("SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;").EtatNonLu(State).Should().BeFalse("les deux moitiés sont posées");
    }

    // Sans la valeur posée, la désinstallation ne sait pas si la chaîne est encore celle de
    // DrDXT, et n'y touche pas (« valeur posée inconnue »). Ses voisins HKCU la portaient déjà (SnapshotAvec).
    [Fact]
    public async Task Snapshot_carries_the_merged_string_it_wrote()
    {
        var t = Build("SwapEffectUpgradeEnable=1;");
        var r = await t.ApplyAsync();

        var pose = PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson!).Value.Should()
            .BeOfType<SetRegistryValueOp>().Subject;
        pose.Value.Should().Be(_elevated.ReceivedCalls().Select(c => c.GetArguments()[0])
            .OfType<SetRegistryValueOp>().Single().Value);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build("");
        t.Id.Should().Be("latency.vrrFlipModelOn");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.TakesEffectAfterReboot.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_and_never_claims_fps()
    {
        Build("").SummaryKey.Should().Be("tweak.latency.vrr_flipmodel.enable");
        Build("").SummaryKey.Should().NotContain("fps");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VRROptimizeEnable=1")]
    [InlineData("AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=0")]
    public void AppliesTo_while_flip_model_is_not_on_whatever_the_display(string? original)
    {
        Build(original).AppliesTo(Hw, State).Should().BeTrue();
    }

    // Flip-model deja la, reste la seule moitie VRR. Elle ne vaut rien sur un
    // ecran qui ne fait pas de VRR : le poste de reference (`SwapEffectUpgradeEnable=1;`) gardait ce
    // levier au plan pour rien, et « rien a tenter » n'etait jamais vrai.

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=1;")]
    [InlineData("AutoHDREnable=1;VRROptimizeEnable=0;SwapEffectUpgradeEnable=1")]
    public void AppliesTo_for_the_VRR_half_only_on_a_display_READ_as_VRR_capable(string original)
    {
        Build(original).AppliesTo(Hw, State with { VrrCapableDisplay = TriState.On }).Should().BeTrue();
    }

    [Theory]
    [InlineData(TriState.Off)]
    [InlineData(TriState.Unknown)]
    public void AppliesTo_is_false_for_the_VRR_half_alone_when_the_display_is_not_read_as_VRR(TriState ecran)
    {
        Build("SwapEffectUpgradeEnable=1;").AppliesTo(Hw, State with { VrrCapableDisplay = ecran })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1")]
    [InlineData("AutoHDREnable=1; swapeffectupgradeenable=1 ;VRROptimizeEnable=1;")]
    public void AppliesTo_is_false_when_both_toggles_are_already_on(string original)
    {
        // Deja regle ⇒ hors du plan, sinon le paiement est demande pour rien.
        Build(original).AppliesTo(Hw, State).Should().BeFalse();
    }

    [Fact]
    public void Illisible_se_dit_non_lu_ni_deja_regle_ni_a_poser()
    {
        // La pose relit la chaîne d'origine et échouerait : proposer le levier « à poser » mettrait au lot un échec certain.
        var t = Build("");
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "illisible"));
        t.AppliesTo(Hw, State).Should().BeFalse();
        t.EtatNonLu(State).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_merges_and_preserves_autohdr_and_flips_existing_vrr()
    {
        // VRR désactivé (0) → passé à 1 ; AutoHDR préservé ; modèle de retournement ajouté.
        var written = await CapturedWrittenValue(Build("AutoHDREnable=1;VRROptimizeEnable=0"));
        written.Should().Be("AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_on_absent_value_writes_just_the_two_keys()
    {
        var written = await CapturedWrittenValue(Build(original: null));
        written.Should().Be("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_preserves_a_value_that_contains_special_chars()
    {
        // HighPerfAdapter porte un identifiant séparé par « & » et doit survivre tel quel à la fusion.
        var written = await CapturedWrittenValue(Build("HighPerfAdapter=10DE&2684&89321043;AutoHDREnable=1"));
        written.Should().Be(
            "HighPerfAdapter=10DE&2684&89321043;AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_snapshot_restores_the_raw_original_string()
    {
        var r = await Build("AutoHDREnable=1;VRROptimizeEnable=0").ApplyAsync();

        r.IsOk.Should().BeTrue();
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be("AutoHDREnable=1;VRROptimizeEnable=0"); // origine exacte
    }

    [Fact]
    public async Task Apply_snapshot_is_a_delete_when_value_was_absent()
    {
        var r = await Build(original: null).ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value
            .Should().BeOfType<DeleteRegistryValueOp>();
    }

    [Fact]
    public async Task Apply_fails_clean_when_registry_read_fails()
    {
        var t = Build("");
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build("");
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build("");
        _registry.ReadString(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public async Task Revert_restores_the_original_string_via_helper()
    {
        var t = Build("AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
        var original = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, PrefsPath, ValueName, RegistryValueType.String, "AutoHDREnable=1");
        var snap = new TweakSnapshot("latency.vrrFlipModelOn", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == ValueName && o.Value == "AutoHDREnable=1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build("");

        var r = await t.RevertAsync(new TweakSnapshot("latency.vrrFlipModelOn", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
    // Banc profond : les deux interrupteurs déjà à 1, l'exercice les met à 0 (la forme que Windows écrit quand on les
    // éteint), puis le levier les rallume, puis la chaîne d'origine revient à l'identique, voisins (AutoHDR) compris.
    [Fact]
    public async Task En_profond_flip_model_et_VRR_deja_poses_sont_exerces_et_la_chaine_revient_a_l_identique()
    {
        const string Origine = "AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;";
        string? chaine = Origine;
        var vues = new List<string?>();
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName).Returns(_ => Result<string?>.Ok(chaine));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            chaine = c.Arg<PrivilegedOp>() switch { SetRegistryValueOp o => o.Value, _ => null };
            vues.Add(chaine);
            return Task.FromResult(Result.Ok());
        });
        var levier = new VrrFlipModelOnTweak(_registry, _elevated);
        var etat = State with { VrrCapableDisplay = TriState.On };
        var journal = new DrDxt.Application.Autopilot.FichierRevertEnAttente(Path.Combine(Path.GetTempPath(), $"drdxt-profond-{Guid.NewGuid():N}.json"));
        DrDxt.Application.Autopilot.ValidationComplete.Lecture Relire(TweakSnapshot sn)
        {
            var p = DrDxt.Application.Tweaks.AppliedStateVerifier.Verifier(sn, _registry);
            return new(p.Verdict switch { DrDxt.Application.Tweaks.EtatApplique.Applied => true,
                DrDxt.Application.Tweaks.EtatApplique.NotApplied => false, _ => (bool?)null }, p.Verdict.ToString());
        }

        var r = await DrDxt.Application.Autopilot.ValidationComplete.ValiderAsync([levier], Hw, etat, journal, Relire, profond: true);

        vues[0].Should().Contain("SwapEffectUpgradeEnable=0").And.Contain("VRROptimizeEnable=0").And.Contain("AutoHDREnable=1");
        chaine.Should().Be(Origine, "la chaîne d'origine, à l'identique");
        r.Texte.Should().Contain("exercé");
        r.NonRemis.Should().BeEmpty();
    }

    [Fact]
    public void Un_interrupteur_pas_encore_pose_n_est_pas_exerce()
        => Build("SwapEffectUpgradeEnable=1;").PreparerExercice(Hw, State with { VrrCapableDisplay = TriState.On }).Should().BeNull();

    private static TweakSnapshot Pose(string? origine, string posee) => new("latency.vrrFlipModelOn",
        PrivilegedOpCodec.Serialize(origine is null
            ? new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            : new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName, RegistryValueType.String, origine)).Value!,
        PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName, RegistryValueType.String, posee)).Value!);

    // La chaîne porte aussi Auto HDR, que le joueur règle lui-même : remettre la chaîne d'origine entière effacerait son
    // Auto HDR. Seules les clés de DrDXT reprennent leur origine, le reste garde la valeur courante.
    [Theory]
    [InlineData("AutoHDREnable=0;SwapEffectUpgradeEnable=1", "AutoHDREnable=1;SwapEffectUpgradeEnable=1")]
    [InlineData(null, "AutoHDREnable=1")]
    public async Task Le_rendu_ne_remet_que_les_cles_de_DrDXT_et_garde_l_Auto_HDR_du_joueur(string? origine, string attendue)
    {
        var t = Build("AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1");

        (await t.RevertAsync(Pose(origine, "AutoHDREnable=0;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1"))).IsOk.Should().BeTrue();

        await _elevated.Received(1).ExecuteAsync(Arg.Is<SetRegistryValueOp>(o => o.Name == ValueName && o.Value == attendue),
            Arg.Any<CancellationToken>());
    }

    // La chaîne courante ne se lit pas : rien n'est écrit par-dessus ce qu'on ne voit pas.
    [Fact]
    public async Task Le_rendu_sans_lire_la_chaine_courante_n_ecrit_rien()
    {
        var t = Build("x");
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName).Returns(Result<string?>.Fail("REG", "refusé"));

        (await t.RevertAsync(Pose("AutoHDREnable=0", "AutoHDREnable=0;VRROptimizeEnable=1"))).IsOk.Should().BeFalse();

        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // Auto HDR basculé depuis par le joueur ne fait pas croire que la pose de DrDXT a changé : seules ses clés se relisent.
    [Fact]
    public void Auto_HDR_bascule_par_le_joueur_laisse_la_pose_a_DrDXT()
    {
        Build("AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1");
        var pose = Pose("AutoHDREnable=0;SwapEffectUpgradeEnable=1", "AutoHDREnable=0;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1");

        DrDxt.Application.Tweaks.TweakReadback.EtatDeLaPose(pose, _registry).Should().Be(DrDxt.Application.Tweaks.PoseRelue.DeDrDxt);

        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName).Returns(Result<string?>.Ok("AutoHDREnable=1;SwapEffectUpgradeEnable=1"));
        DrDxt.Application.Tweaks.TweakReadback.EtatDeLaPose(pose, _registry).Should().Be(DrDxt.Application.Tweaks.PoseRelue.ALOrigine,
            "témoin : les clés de DrDXT revenues à l'origine");
    }
}
