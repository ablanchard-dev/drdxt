using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// InProcessElevatedClient : l'exécuteur privilégié en processus du réconciliateur au démarrage.
//
// Contrat de sécurité testé : il fait passer la vraie OpAllowlist en dernière porte avant
// l'exécuteur, donc une op hors liste n'atteint jamais l'exécuteur (aucun contact avec
// l'OS) ; une op de la liste est transmise et le Result de l'exécuteur remonte fidèlement
// (échec compris : jamais avalé, jamais un succès inventé). Un faux IPrivilegedOpExecutor
// remplace les vraies actions Windows pour que ces tests ne touchent jamais à l'OS.
// -----------------------------------------------------------------------
public sealed class InProcessElevatedClientTests
{
    private sealed class FakeExecutor : IPrivilegedOpExecutor
    {
        public int Calls;
        public PrivilegedOp? LastOp;
        private readonly Result _result;

        public FakeExecutor(Result? result = null) => _result = result ?? Result.Ok();

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Calls++;
            LastOp = op;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task A_non_allowlisted_op_is_denied_before_the_executor_runs()
    {
        // Un GUID de plan d'alimentation inconnu est structurellement hors liste blanche.
        var executor = new FakeExecutor();
        var client = new InProcessElevatedClient(new OpAllowlist(), executor);

        var result = await client.ExecuteAsync(new SetActivePowerPlanOp(Guid.NewGuid()), default);

        result.IsOk.Should().BeFalse("a non-allowlisted op must be refused");
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        executor.Calls.Should().Be(0, "the gate must run BEFORE the executor — no partial action, no OS touch");
    }

    [Fact]
    public async Task A_null_op_is_denied_before_the_executor_runs()
    {
        var executor = new FakeExecutor();
        var client = new InProcessElevatedClient(new OpAllowlist(), executor);

        var result = await client.ExecuteAsync(null!, default);

        result.IsOk.Should().BeFalse("a null op must be refused");
        executor.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_allowlisted_op_reaches_the_executor_and_its_ok_result_is_returned()
    {
        // PingOp est dans la liste blanche d'OpAllowlist et sans effet.
        var executor = new FakeExecutor(Result.Ok());
        var client = new InProcessElevatedClient(new OpAllowlist(), executor);

        var op = new PingOp();
        var result = await client.ExecuteAsync(op, default);

        result.IsOk.Should().BeTrue();
        executor.Calls.Should().Be(1);
        executor.LastOp.Should().BeSameAs(op, "the validated op is forwarded unchanged");
    }

    [Fact]
    public async Task An_executor_failure_is_surfaced_not_swallowed()
    {
        var failure = Result.Fail(ErrorCodes.HelperOpFailed, "native action failed");
        var executor = new FakeExecutor(failure);
        var client = new InProcessElevatedClient(new OpAllowlist(), executor);

        var result = await client.ExecuteAsync(new PingOp(), default);

        result.IsOk.Should().BeFalse("a real executor failure must propagate — never a faked success");
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var act1 = () => new InProcessElevatedClient(null!, new FakeExecutor());
        var act2 = () => new InProcessElevatedClient(new OpAllowlist(), null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }
}
