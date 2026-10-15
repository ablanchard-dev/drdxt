using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// La boucle fermée après redémarrage : un réglage qui a pris → succès + marqueur nettoyé ; un
/// réglage qui n'a pas pris → DidNotTake + carte de récupération + marqueur gardé ; un réglage
/// illisible par l'OS → Unreadable honnête (jamais un faux succès) ; pas de marqueur → rien,
/// idempotent. Le vérificateur n'écrit jamais le BIOS.
/// </summary>
public class BiosLoopVerifierTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 25, 10, 30, 0, TimeSpan.Zero);

    // ── Une doublure de magasin pilotée directement (sans disque) : le vérificateur ne regarde que
    //    Load() et le fait qu'il Clear() en cas de succès, pas en cas d'échec. ──
    private sealed class FakeStore : IPendingBiosChangeStore
    {
        private PendingBiosChange? _change;
        public bool LoadFails { get; init; }
        public bool ClearFails { get; init; }
        public bool ClearCalled { get; private set; }
        public bool MarkerPresent => _change is not null;

        public FakeStore(PendingBiosChange? change = null) => _change = change;

        public Result Save(PendingBiosChange change) { _change = change; return Result.Ok(); }

        public Result<PendingBiosChange?> Load()
            => LoadFails
                ? Result<PendingBiosChange?>.Fail(ErrorCodes.BiosPendingChangeCorrupt, "corrupt")
                : Result<PendingBiosChange?>.Ok(_change);

        public Result Clear()
        {
            ClearCalled = true;
            if (ClearFails) return Result.Fail(ErrorCodes.BiosPendingChangeIo, "forced");
            _change = null;
            return Result.Ok();
        }
    }

    // ── Une identité de machine pilotable pour que l'identifiant du vérificateur corresponde (ou,
    //    pour le test du marqueur étranger, ne corresponde volontairement pas) au « machine-abc » du marqueur. ──
    private sealed class FakeMachineId(string current) : IBiosMachineId
    {
        public string Current { get; } = current;
    }

    /// <summary>Construit un vérificateur dont l'identifiant de machine courant correspond au marqueur par défaut.</summary>
    private static BiosLoopVerifier Verifier(IPendingBiosChangeStore store, string machineId = "machine-abc")
        => new(store, new FakeMachineId(machineId));

    private static PendingBiosChange Pending(params PendingSettingTarget[] targets)
        => new(targets, Now, "machine-abc");

    private static SettingsState State(TriState xmp = TriState.Unknown, TriState rebar = TriState.Unknown)
        => new(xmp, rebar, "GUID", TriState.Unknown, TriState.Off, Array.Empty<int>(), TriState.Unknown);

    [Fact]
    public void No_marker_is_an_idempotent_no_op()
    {
        var verifier = Verifier(new FakeStore(change: null));

        var r = verifier.Verify(State(xmp: TriState.On));

        r.IsOk.Should().BeTrue();
        r.Value!.HadPendingChange.Should().BeFalse();
        r.Value.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeFalse();
        r.Value.Outcomes.Should().BeEmpty();
        r.Value.MarkerCleared.Should().BeFalse();
    }

    [Fact]
    public void Setting_that_took_yields_success_and_clears_the_marker()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));
        var verifier = Verifier(store);

        var r = verifier.Verify(State(xmp: TriState.On));

        r.IsOk.Should().BeTrue();
        r.Value!.AllTook.Should().BeTrue();
        r.Value.ShowRecoveryCard.Should().BeFalse();
        r.Value.RecoveryCardKeys.Should().BeEmpty();
        r.Value.MarkerCleared.Should().BeTrue();
        r.Value.Outcomes.Should().ContainSingle()
            .Which.Verdict.Should().Be(BiosSettingVerdict.Took);
        store.ClearCalled.Should().BeTrue();
        store.MarkerPresent.Should().BeFalse();
    }

    [Fact]
    public void All_settings_took_across_multiple_targets()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On),
            new PendingSettingTarget(FirmwareSettingKind.ReBar, TriState.On)));
        var verifier = Verifier(store);

        var r = verifier.Verify(State(xmp: TriState.On, rebar: TriState.On));

        r.Value!.AllTook.Should().BeTrue();
        r.Value.MarkerCleared.Should().BeTrue();
        r.Value.Outcomes.Should().OnlyContain(o => o.Verdict == BiosSettingVerdict.Took);
    }

    [Fact]
    public void Setting_that_did_not_take_shows_recovery_card_and_keeps_the_marker()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));
        var verifier = Verifier(store);

        // Attendu actif, mais la relecture fraîche dit toujours désactivé → ça n'a pas pris.
        var r = verifier.Verify(State(xmp: TriState.Off));

        r.IsOk.Should().BeTrue();
        r.Value!.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeTrue();
        r.Value.RecoveryCardKeys.Should().BeEquivalentTo(BiosLoopStringKeys.RecoveryCard);
        r.Value.MarkerCleared.Should().BeFalse();
        r.Value.Outcomes.Should().ContainSingle()
            .Which.Verdict.Should().Be(BiosSettingVerdict.DidNotTake);
        // Honnêteté : marqueur gardé, pour que la boucle rejoue à la prochaine tentative.
        store.ClearCalled.Should().BeFalse();
        store.MarkerPresent.Should().BeTrue();
    }

    [Fact]
    public void Setting_that_is_not_readable_is_honestly_unreadable_never_false_success()
    {
        // CSM n'est pas dans SettingsState → la lecture OS ne peut jamais le confirmer.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.Csm, TriState.Off)));
        var verifier = Verifier(store);

        var r = verifier.Verify(State());

        r.IsOk.Should().BeTrue();
        r.Value!.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeTrue();
        r.Value.MarkerCleared.Should().BeFalse();
        var outcome = r.Value.Outcomes.Should().ContainSingle().Subject;
        outcome.Verdict.Should().Be(BiosSettingVerdict.Unreadable);
        outcome.ObservedState.Should().Be(TriState.Unknown);
        store.ClearCalled.Should().BeFalse();
    }

    [Fact]
    public void A_readable_unknown_read_is_unreadable_not_a_did_not_take()
    {
        // XMP se traduit bien, mais la relecture fraîche est revenue Unknown → Unreadable honnête.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));
        var verifier = Verifier(store);

        var r = verifier.Verify(State(xmp: TriState.Unknown));

        r.Value!.Outcomes.Should().ContainSingle()
            .Which.Verdict.Should().Be(BiosSettingVerdict.Unreadable);
        r.Value.ShowRecoveryCard.Should().BeTrue();
    }

    [Fact]
    public void Mixed_one_took_one_did_not_is_not_all_took_and_keeps_marker()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On),
            new PendingSettingTarget(FirmwareSettingKind.ReBar, TriState.On)));
        var verifier = Verifier(store);

        var r = verifier.Verify(State(xmp: TriState.On, rebar: TriState.Off));

        r.Value!.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeTrue();
        r.Value.MarkerCleared.Should().BeFalse();
        r.Value.Outcomes.Should().Contain(o =>
            o.Setting == FirmwareSettingKind.XmpExpo && o.Verdict == BiosSettingVerdict.Took);
        r.Value.Outcomes.Should().Contain(o =>
            o.Setting == FirmwareSettingKind.ReBar && o.Verdict == BiosSettingVerdict.DidNotTake);
        store.MarkerPresent.Should().BeTrue();
    }

    [Fact]
    public void Corrupt_marker_propagates_as_a_clean_failure()
    {
        var verifier = Verifier(new FakeStore { LoadFails = true });

        var r = verifier.Verify(State());

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Clear_io_fault_on_success_surfaces_a_failure_not_a_fake_ok()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)))
        { ClearFails = true };
        var verifier = Verifier(store);

        var r = verifier.Verify(State(xmp: TriState.On));

        // Tout a pris, mais le marqueur n'a pas pu être nettoyé → échec honnête.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeIo);
    }

    [Fact]
    public void Null_state_fails_clean()
    {
        var verifier = Verifier(new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On))));

        var r = verifier.Verify(null!);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosInputInvalid);
    }

    [Fact]
    public void Foreign_machine_marker_is_not_honored_and_left_untouched()
    {
        // Le marqueur dit « machine-abc » ; cette machine annonce un autre identifiant → un marqueur
        // copié d'une autre machine ne doit jamais être honoré ici.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));
        var verifier = Verifier(store, machineId: "some-other-machine");

        // Même si XMP se lit actif (ça « prendrait »), le marqueur étranger n'est pas évalué.
        var r = verifier.Verify(State(xmp: TriState.On));

        r.IsOk.Should().BeTrue();
        r.Value!.HadPendingChange.Should().BeFalse("a foreign marker is treated as nothing-pending");
        r.Value.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeFalse();
        r.Value.Outcomes.Should().BeEmpty();
        r.Value.MarkerCleared.Should().BeFalse();
        // Le marqueur étranger est laissé intact (pas effacé) : ce n'est pas à nous de le retirer.
        store.ClearCalled.Should().BeFalse();
        store.MarkerPresent.Should().BeTrue();
    }

    [Fact]
    public void Machine_id_compare_is_case_and_whitespace_insensitive()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));
        // Même machine, casse ou espaces différents → toujours honoré (le changement a pris).
        var verifier = Verifier(store, machineId: "  MACHINE-ABC ");

        var r = verifier.Verify(State(xmp: TriState.On));

        r.Value!.HadPendingChange.Should().BeTrue();
        r.Value.AllTook.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Le juge travaille sur le delta mesuré, pas sur deux inférences.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Un état qui porte aussi l'horloge mémoire dure et la lecture du CSM.</summary>
    private static SettingsState StateWith(
        TriState xmp = TriState.Unknown,
        TriState rebar = TriState.Unknown,
        int mhz = 0,
        TriState csm = TriState.Unknown)
        => new(xmp, rebar, "GUID", TriState.Unknown, TriState.Off, Array.Empty<int>(), TriState.Unknown,
               MemoryConfiguredMhz: mhz, Csm: csm);

    [Fact]
    public void Delta_kills_the_false_success_the_TriState_compare_would_report()
    {
        // Le défaut qui justifie ce lot : sur un kit 4000 le plancher JEDEC (3200) est dépassé à
        // 3600 comme à 3600, donc XmpInferencer répond actif avant et après. La comparaison TriState
        // voit observé(On) == attendu(On) et déclare Took, alors que rien n'a bougé.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 3600)));
        var verifier = Verifier(store);

        var r = verifier.Verify(StateWith(xmp: TriState.On, mhz: 3600));

        r.Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.DidNotTake);
        r.Value.AllTook.Should().BeFalse();
        r.Value.ShowRecoveryCard.Should().BeTrue();
        store.MarkerPresent.Should().BeTrue();   // marqueur gardé : la boucle peut rejouer
    }

    [Fact]
    public void Delta_confirms_a_real_move_even_when_the_inference_is_unreadable()
    {
        // Le cas miroir : l'inférence a échoué (Unknown → le chemin TriState dirait Unreadable et
        // retiendrait le succès), mais la machine est mesurablement passée de 2133 à 3800.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 2133)));
        var verifier = Verifier(store);

        var r = verifier.Verify(StateWith(xmp: TriState.Unknown, mhz: 3800));

        r.Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Took);
        r.Value.AllTook.Should().BeTrue();
        store.MarkerPresent.Should().BeFalse();
    }

    [Fact]
    public void Disabling_is_judged_on_the_quantity_going_DOWN()
    {
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.Off, ObservedBefore: 3800)));

        Verifier(store).Verify(StateWith(mhz: 2133))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Took);
    }

    [Fact]
    public void An_unread_quantity_is_never_treated_as_zero_MHz()
    {
        // mhz = 0 veut dire « jamais lu », pas « 0 MHz ». Aucun delta ne peut en être jugé : la boucle
        // retombe sur la comparaison TriState au lieu d'inventer une chute de 3600 à 0.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 3600)));

        Verifier(store).Verify(StateWith(xmp: TriState.Unknown, mhz: 0))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Unreadable);
    }

    [Fact]
    public void Above4G_is_proven_On_by_an_active_ReBAR_and_never_fabricated_Off()
    {
        var took = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.Above4G, TriState.On)));
        Verifier(took).Verify(StateWith(rebar: TriState.On))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Took);

        // ReBAR désactivé ne prouve rien sur Above4G (il peut être actif avec ReBAR désactivé) → Unreadable honnête.
        var unknown = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.Above4G, TriState.On)));
        Verifier(unknown).Verify(StateWith(rebar: TriState.Off))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Unreadable);
    }

    [Fact]
    public void Csm_is_passed_through_from_SettingsState_when_a_reader_provides_it()
    {
        // Ce test injecte la valeur et verifie une simple retransmission : il ne touche ni
        // SettingsStateReader ni IsUefi, et son nom ne pretend pas couvrir une derivation.
        // Il n'y a d'ailleurs pas de derivation : le lecteur rend Unknown, faute d'une
        // source capable de distinguer « legacy » de « pas su ».
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.Csm, TriState.On)));

        Verifier(store).Verify(StateWith(csm: TriState.On))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Took);
    }

    [Fact]
    public void A_baseline_of_zero_is_never_treated_as_a_measurement()
    {
        // 0 = « jamais lu », des deux cotes. La garde manquait sur `before` : la source qui
        // alimente ce champ (SettingsState.MemoryConfiguredMhz) documente elle-meme que 0
        // signifie non lu. Sans elle : before=0, after=3800 > 0, verdict Took -- un succes
        // fabrique depuis une ligne de base inexistante, dans la fonction dont la raison
        // d'etre est de refuser exactement ca.
        var store = new FakeStore(Pending(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 0)));

        Verifier(store).Verify(StateWith(xmp: TriState.Unknown, mhz: 3800))
            .Value!.Outcomes.Single().Verdict.Should().Be(BiosSettingVerdict.Unreadable);
    }

    // L'A/B memoire du retour exige une mesure d'avant prise au depart. Le verdict
    // porte donc l'heure du marqueur, que le changement ait pris ou non.
    [Theory]
    [InlineData(TriState.On)]
    [InlineData(TriState.Off)]
    public void Le_verdict_porte_l_heure_du_depart(TriState relu)
    {
        var store = new FakeStore(Pending(new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On)));

        var r = Verifier(store).Verify(State(xmp: relu));

        r.Value!.DepartUtc.Should().Be(Now);
    }

    [Fact]
    public void Sans_voyage_il_n_y_a_pas_d_heure_de_depart()
        => Verifier(new FakeStore(change: null)).Verify(State()).Value!.DepartUtc.Should().BeNull();
}
