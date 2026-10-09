using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Platform;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// L0.2 — le protocole en deux temps du voyage dans le BIOS.
//
// Ce que ces tests verrouillent tient en une phrase : on n'envoie pas quelqu'un dans son BIOS
// si on ne pourra rien lui prouver au retour. L'ordre (marqueur d'abord, redemarrage ensuite)
// et le nettoyage d'un marqueur dont le voyage n'a pas eu lieu sont les deux endroits ou ca se
// casse silencieusement -- dans les deux cas la machine repart et le produit ment au retour.
// -----------------------------------------------------------------------
public sealed class FirmwareTripTests
{
    private const int Delay = 10;

    private sealed class FakeStore : IPendingBiosChangeStore
    {
        private readonly Result _saveResult;
        private readonly Result _clearResult;
        public FakeStore(Result? save = null, Result? clear = null)
        {
            _saveResult = save ?? Result.Ok();
            _clearResult = clear ?? Result.Ok();
        }

        public PendingBiosChange? Saved { get; private set; }
        public int ClearCount { get; private set; }
        /// <summary>La trace d'ordre : "save" puis "clear", dans l'ordre reel des appels.</summary>
        public List<string> Calls { get; } = new();

        public Result Save(PendingBiosChange change)
        {
            Calls.Add("save");
            if (_saveResult.IsOk) Saved = change;
            return _saveResult;
        }

        public Result<PendingBiosChange?> Load() => Result<PendingBiosChange?>.Ok(Saved);

        public Result Clear()
        {
            Calls.Add("clear");
            ClearCount++;
            if (_clearResult.IsOk) Saved = null;
            return _clearResult;
        }
    }

    private sealed class FakeElevated : IElevatedClient
    {
        private readonly Result _result;
        private readonly List<string> _shared;
        public FakeElevated(Result result, List<string> sharedCalls)
        {
            _result = result;
            _shared = sharedCalls;
        }

        public PrivilegedOp? Sent { get; private set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            _shared.Add("reboot");
            Sent = op;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeVerifier : IBiosLoopVerifier
    {
        private readonly Result<BiosLoopResult> _result;
        public FakeVerifier(Result<BiosLoopResult> result) => _result = result;
        public SettingsState? Seen { get; private set; }
        public Result<BiosLoopResult> Verify(SettingsState freshState)
        {
            Seen = freshState;
            return _result;
        }
    }

    private sealed class FixedClock : IBiosClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 2, 5, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedMachine : IBiosMachineId
    {
        public string Current => "PC-DE-TEST";
    }

    private static SettingsState State(int memMhz = 2133) => new(
        XmpExpo: TriState.Off,
        ReBar: TriState.Off,
        PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
        Hags: TriState.Off,
        GameMode: TriState.On,
        ParkedCoreIndexes: Array.Empty<int>(),
        Vrr: TriState.Unknown,
        MemoryConfiguredMhz: memMhz);

    private static BiosFinding Finding(
        FirmwareSettingKind kind = FirmwareSettingKind.XmpExpo,
        BiosRecommendedAction action = BiosRecommendedAction.Enable,
        BiosMatrixClass matrix = BiosMatrixClass.Guide) =>
        new(kind, Severity.Major, "t", "r", matrix, TriState.Off, action, "a", null, false);

    private static (FirmwareTrip trip, FakeStore store, FakeElevated elevated) Build(
        Result? save = null, Result? reboot = null, Result? clear = null)
    {
        var store = new FakeStore(save, clear);
        var elevated = new FakeElevated(reboot ?? Result.Ok(), store.Calls);
        var trip = new FirmwareTrip(
            store,
            new FakeVerifier(Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending)),
            elevated,
            new FixedClock(),
            new FixedMachine());
        return (trip, store, elevated);
    }

    [Fact]
    public async Task The_marker_is_written_BEFORE_the_reboot_is_requested()
    {
        // L'inverse laisse une fenetre ou la machine part dans le BIOS sans que rien ne sache
        // ce qu'on y attendait : l'utilisateur fait son reglage et il n'y a plus rien a verifier.
        var (trip, store, elevated) = Build();

        var r = await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        r.IsOk.Should().BeTrue();
        store.Calls.Should().Equal("save", "reboot");
        elevated.Sent.Should().BeOfType<RebootToFirmwareSetupOp>()
            .Which.DelaySeconds.Should().Be(Delay);
    }

    [Fact]
    public async Task A_marker_that_cannot_be_written_cancels_the_trip_entirely()
    {
        // Envoyer quelqu'un dans son BIOS en sachant qu'on ne pourra rien lui prouver au
        // retour, c'est vendre la preuve sans la livrer.
        var (trip, store, elevated) = Build(save: Result.Fail(ErrorCodes.BiosPendingChangeCorrupt, "disque plein"));

        var r = await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        r.IsOk.Should().BeFalse();
        elevated.Sent.Should().BeNull("aucun redemarrage ne doit partir sans marqueur");
        store.Calls.Should().Equal("save");
    }

    [Fact]
    public async Task A_refused_reboot_does_not_leave_a_marker_behind()
    {
        // Sinon le marqueur survit a un voyage qui n'a pas eu lieu, et la prochaine ouverture
        // de l'app rend un verdict sur un changement que personne n'a fait.
        var (trip, store, _) = Build(reboot: Result.Fail(ErrorCodes.HelperOpFailed, "firmware refuse"));

        var r = await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("firmware refuse");
        store.ClearCount.Should().Be(1);
        store.Saved.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_cleanup_is_said_on_top_of_the_original_reason()
    {
        // Un marqueur perime qui reste est un mensonge differe : le taire serait pire que
        // l'echec d'origine.
        var (trip, _, _) = Build(
            reboot: Result.Fail(ErrorCodes.HelperOpFailed, "firmware refuse"),
            clear: Result.Fail(ErrorCodes.IoFailure, "fichier verrouille"));

        var r = await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("firmware refuse");
        r.Error!.Value.Message.Should().Contain("marker could not be removed");
    }

    [Fact]
    public async Task Nothing_actionable_means_no_marker_and_no_reboot()
    {
        // Un constat informatif ne se verifie pas au retour : redemarrer pour lui ne prouve rien.
        var (trip, store, elevated) = Build();
        var informational = Finding(action: BiosRecommendedAction.None, matrix: BiosMatrixClass.Read);

        var r = await trip.ArmAndRebootAsync(new[] { informational }, State(), Delay, default);

        r.IsOk.Should().BeFalse();
        store.Calls.Should().BeEmpty();
        elevated.Sent.Should().BeNull();
    }

    [Fact]
    public async Task A_route_change_is_not_turned_into_a_boolean_expectation()
    {
        // SetSupportedRoute (CPPC) ne se ramene pas a On/Off. L'inclure fabriquerait une
        // attente que le verifieur trancherait au hasard au retour.
        var (trip, store, elevated) = Build();
        var route = Finding(FirmwareSettingKind.X3dCppc, BiosRecommendedAction.SetSupportedRoute);

        var r = await trip.ArmAndRebootAsync(new[] { route }, State(), Delay, default);

        r.IsOk.Should().BeFalse();
        store.Saved.Should().BeNull();
        elevated.Sent.Should().BeNull();
    }

    [Fact]
    public async Task The_marker_carries_the_measured_memory_clock_not_just_the_inference()
    {
        // 2133 -> 3800 est un fait ; probably-On -> probably-Off est un pari sur un pari.
        var (trip, store, _) = Build();

        await trip.ArmAndRebootAsync(new[] { Finding() }, State(memMhz: 2133), Delay, default);

        var target = store.Saved!.Targets.Should().ContainSingle().Subject;
        target.Setting.Should().Be(FirmwareSettingKind.XmpExpo);
        target.ExpectedState.Should().Be(TriState.On);
        target.ObservedBefore.Should().Be(2133);
        store.Saved.MachineId.Should().Be("PC-DE-TEST");
        store.Saved.CreatedUtc.Should().Be(new DateTimeOffset(2026, 9, 2, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task An_unread_memory_clock_stays_unread_instead_of_becoming_zero_MHz()
    {
        // 0 = jamais lu. Le passer tel quel donnerait une ligne de base inexistante, dont le
        // verifieur deduirait un succes fabrique (c'est le bug que sa garde `before == 0` evite).
        var (trip, store, _) = Build();

        await trip.ArmAndRebootAsync(new[] { Finding() }, State(memMhz: 0), Delay, default);

        store.Saved!.Targets.Should().ContainSingle().Which.ObservedBefore.Should().BeNull();
    }

    [Fact]
    public void The_return_verdict_is_computed_against_the_FRESH_state()
    {
        var verifier = new FakeVerifier(Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending));
        var calls = new List<string>();
        var trip = new FirmwareTrip(
            new FakeStore(), verifier, new FakeElevated(Result.Ok(), calls),
            new FixedClock(), new FixedMachine());
        var fresh = State(memMhz: 3800);

        var r = trip.VerifyOnReturn(fresh);

        r.IsOk.Should().BeTrue();
        verifier.Seen.Should().BeSameAs(fresh);
    }

    // 26/09/2026 : se retracter pendant le delai. Le voyage n'aura pas lieu, donc le marqueur part aussi ;
    // mais seulement si l'annulation a reussi, sinon le retour doit encore etre verifie.
    [Fact]
    public async Task Annuler_annule_le_redemarrage_PUIS_retire_le_marqueur()
    {
        var (trip, store, elevated) = Build();
        await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        var r = await trip.AnnulerAsync(default);

        r.IsOk.Should().BeTrue();
        elevated.Sent.Should().BeOfType<CancelPendingFirmwareBootOp>();
        store.Calls.Should().Equal("save", "reboot", "reboot", "clear");
        store.Saved.Should().BeNull();
    }

    [Fact]
    public async Task Une_annulation_refusee_garde_le_marqueur()
    {
        var (trip, store, _) = Build(reboot: Result.Fail(ErrorCodes.HelperOpFailed, "refus"));

        var r = await trip.AnnulerAsync(default);

        r.IsOk.Should().BeFalse();
        store.ClearCount.Should().Be(0, "le redemarrage a peut-etre lieu : son retour doit rester verifiable");
    }

    [Fact]
    public async Task Oublier_retire_le_marqueur()
    {
        var (trip, store, _) = Build();
        await trip.ArmAndRebootAsync(new[] { Finding() }, State(), Delay, default);

        trip.Oublier().IsOk.Should().BeTrue();

        store.Saved.Should().BeNull();
        store.ClearCount.Should().Be(1);
    }
}
