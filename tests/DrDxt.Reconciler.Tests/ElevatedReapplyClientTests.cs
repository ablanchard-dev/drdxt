using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T32 — ElevatedReapplyClient maps a reconcilable tweakId to an existing typed op
// and dispatches via IElevatedClient. An unknown tweakId fails before any IPC (I3).
// -----------------------------------------------------------------------
public sealed class ElevatedReapplyClientTests
{
    // A recording IElevatedClient — captures every op it is asked to execute, so we
    // can prove (a) a known tweak dispatches the RIGHT typed op, and (b) an unknown
    // tweak dispatches nothing.
    private sealed class RecordingElevatedClient : IElevatedClient
    {
        public List<PrivilegedOp> Sent { get; } = new();
        public Result Next { get; set; } = Result.Ok();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Sent.Add(op);
            return Task.FromResult(Next);
        }
    }

    // A mapper backed by an explicit table — the production shape, configurable.
    private sealed class TableMapper : IReapplyOpMapper
    {
        private readonly Dictionary<string, Func<string, PrivilegedOp>> _table = new(StringComparer.Ordinal);
        public TableMapper Map(string id, Func<string, PrivilegedOp> build)
        {
            _table[id] = build;
            return this;
        }
        public Result<PrivilegedOp> Map(string tweakId, string desiredValueJson)
            => _table.TryGetValue(tweakId, out var build)
                ? Result<PrivilegedOp>.Ok(build(desiredValueJson))
                : Result<PrivilegedOp>.Fail(ReconcilerErrorCodes.TweakNotAllowlisted, "unknown tweak");
    }

    [Fact]
    public async Task Reapply_KnownTweak_DispatchesTheMatchingTypedOp()
    {
        var elevated = new RecordingElevatedClient();
        var mapper = new TableMapper().Map("reflex.lowlatency", _ =>
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SOFTWARE\X", "Mode", RegistryValueType.DWord, "2"));
        var client = new ElevatedReapplyClient(mapper, elevated);

        var result = await client.ReapplyAsync("reflex.lowlatency", "{\"v\":2}", default);

        result.IsOk.Should().BeTrue();
        elevated.Sent.Should().ContainSingle().Which.Should().BeOfType<SetRegistryValueOp>();
    }

    [Fact]
    public async Task Reapply_UnknownTweak_FailsBeforeAnyIpc()
    {
        var elevated = new RecordingElevatedClient();
        var mapper = new TableMapper(); // empty
        var client = new ElevatedReapplyClient(mapper, elevated);

        var result = await client.ReapplyAsync("totally.unknown", "{}", default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.TweakNotAllowlisted);
        elevated.Sent.Should().BeEmpty("no op is ever sent to the helper for an unknown tweak (I3)");
    }

    [Fact]
    public async Task Reapply_PropagatesHelperFailure()
    {
        var elevated = new RecordingElevatedClient { Next = Result.Fail(ErrorCodes.HelperOpFailed, "boom") };
        var mapper = new TableMapper().Map("svc.x3d", _ =>
            new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start));
        var client = new ElevatedReapplyClient(mapper, elevated);

        var result = await client.ReapplyAsync("svc.x3d", "\"Running\"", default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        elevated.Sent.Should().ContainSingle("the op WAS dispatched; the helper rejected it");
    }
}
