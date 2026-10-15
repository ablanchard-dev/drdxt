using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// ElevatedReapplyClient associe un tweakId réconciliable à une op typée existante
// et l'envoie via IElevatedClient. Un tweakId inconnu échoue avant tout IPC.
// -----------------------------------------------------------------------
public sealed class ElevatedReapplyClientTests
{
    // Un IElevatedClient enregistreur : capture chaque op qu'on lui demande d'exécuter, pour
    // prouver (a) qu'un levier connu envoie la bonne op typée, et (b) qu'un levier inconnu
    // n'envoie rien.
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

    // Un mappeur adossé à une table explicite : la forme de production, configurable.
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
