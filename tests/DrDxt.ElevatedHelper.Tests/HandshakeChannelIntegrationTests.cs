using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// End-to-end over a real named pipe (not elevated, not a spawned process):
// HandshakeAcceptingServer (negotiates the ephemeral key after caller-auth) <->
// HandshakingElevatedClient (connects, handshakes, then signs a typed op with the
// negotiated key). Proves the §9.13 handshake feeds the PROVEN typed protocol
// unchanged, with NO env-var pre-shared key.
//
// A unique pipe name per test avoids collisions; nothing here elevates.
// -----------------------------------------------------------------------
public sealed class HandshakeChannelIntegrationTests
{
    private sealed class AllowAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class DenyAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) =>
            Result.Fail(ErrorCodes.ElevationDenied, "denied");
    }

    private sealed class RecordingExecutor : IPrivilegedOpExecutor
    {
        public PrivilegedOp? Executed { get; private set; }
        public bool WasInvoked => Executed is not null;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Executed = op;
            return Task.FromResult(Result.Ok());
        }
    }

    private static string UniquePipe() => "DrDXT.HsTest." + Guid.NewGuid().ToString("N");

    // Le serveur de ces tests tourne dans ce processus : son image attendue est donc celle
    // du processus de test. Cet argument n'est pas decoratif — depuis le 11/09/2026 le client
    // AUTHENTIFIE le serveur du tube avant de lui parler, et ces cinq tests sont la preuve
    // que le chemin legitime passe (le refus, lui, est prouve par TubeUsurpeTests).
    private static string CeProcessus => Environment.ProcessPath!;

    // --- happy path: authorized caller -> handshake -> allowlisted op executes ---
    [Fact]
    public async Task Authorized_handshake_then_allowlisted_op_executes()
    {
        var pipe = UniquePipe();
        var exec = new RecordingExecutor();
        var server = new HandshakeAcceptingServer(
            new AllowAuth(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, pipe);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.AcceptOneAsync(cts.Token);

        var client = new HandshakingElevatedClient(pipe, CeProcessus);
        var result = await client.ExecuteAsync(
            new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), cts.Token);

        await serverTask;

        result.IsOk.Should().BeTrue();
        exec.WasInvoked.Should().BeTrue();
        exec.Executed.Should().BeOfType<SetActivePowerPlanOp>();
    }

    // --- caller-auth denied -> no key, the op never executes, client fails clean ---
    [Fact]
    public async Task Unauthorized_caller_gets_no_channel_and_op_never_executes()
    {
        var pipe = UniquePipe();
        var exec = new RecordingExecutor();
        var server = new HandshakeAcceptingServer(
            new DenyAuth(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, pipe);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.AcceptOneAsync(cts.Token);

        var client = new HandshakingElevatedClient(pipe, CeProcessus);
        var result = await client.ExecuteAsync(
            new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), cts.Token);

        await serverTask;

        result.IsOk.Should().BeFalse();
        exec.WasInvoked.Should().BeFalse("a denied caller must never reach the executor");
    }

    // 26/09/2026 : le tube n'a qu'une instance, et la lecture de la poignee de main n'avait aucun delai. Un
    // programme du compte qui se connecte sans rien dire tenait le helper indefiniment : plus rien a appliquer,
    // plus rien a DEFAIRE. La connexion muette expire ; la boucle d'acceptation, elle, continue.
    [Fact]
    public async Task Un_client_muet_libere_le_tube_au_bout_du_delai_de_lecture()
    {
        var pipe = UniquePipe();
        var exec = new RecordingExecutor();
        var server = new HandshakeAcceptingServer(
            new AllowAuth(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, pipe,
            delaiDeLecture: TimeSpan.FromMilliseconds(300));

        using var arret = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.AcceptOneAsync(arret.Token);

        using var muet = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
        await muet.ConnectAsync(5000);

        var fini = await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));
        fini.Should().BeSameAs(serverTask, "la connexion muette doit expirer, pas tenir le tube");
        await serverTask; // ne leve pas : l'expiration n'est pas un arret du service
        arret.IsCancellationRequested.Should().BeFalse();
        exec.WasInvoked.Should().BeFalse();
    }

    // --- a non-allowlisted op is still refused after a valid handshake ---
    [Fact]
    public async Task Non_allowlisted_op_refused_after_valid_handshake()
    {
        var pipe = UniquePipe();
        var exec = new RecordingExecutor();
        var server = new HandshakeAcceptingServer(
            new AllowAuth(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, pipe);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.AcceptOneAsync(cts.Token);

        var client = new HandshakingElevatedClient(pipe, CeProcessus);
        // A blacklisted target (disabling Defender) must never be allowlisted.
        var evil = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4");
        var result = await client.ExecuteAsync(evil, cts.Token);

        await serverTask;

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- client fails clean when no server is listening (no helper) ---
    [Fact]
    public async Task Client_fails_clean_when_no_server_is_listening()
    {
        var client = new HandshakingElevatedClient(UniquePipe(), CeProcessus, connectTimeoutMs: 300);
        var result = await client.ExecuteAsync(
            new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), default);
        result.IsOk.Should().BeFalse();
        // No pipe ever came up → HELPER_UNREACHABLE (FIX 1 connect-retry deadline).
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable);
    }

    // --- FIX 1: the first op absorbs helper STARTUP LATENCY via the connect-retry.
    //     The client begins its op while no pipe exists; the server starts listening
    //     a bit later (simulating the just-launched helper). The bounded connect-retry
    //     reaches it on a subsequent attempt — ONE accept, no separate probe. ---
    [Fact]
    public async Task First_op_retries_connect_until_a_late_starting_helper_serves()
    {
        var pipe = UniquePipe();
        var exec = new RecordingExecutor();

        // Client starts NOW; the pipe does not exist yet.
        // 28/09/2026 : 4 s ne suffisaient pas en CI. Sur un runner saturé, la reprise après le délai de
        // 300 ms (celle qui lance le serveur) arrivait après la fin du budget du client, et le serveur
        // attendait jusqu'à l'annulation. 12 s restent sous les 15 s ; la réussite ne dure pas plus.
        var client = new HandshakingElevatedClient(
            pipe, CeProcessus, connectTimeoutMs: 12000, connectRetryIntervalMs: 50);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clientTask = client.ExecuteAsync(
            new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), cts.Token);

        // The "helper" comes up ~300ms late — after the client has already started and
        // failed at least one connect attempt.
        await Task.Delay(300, cts.Token);
        var server = new HandshakeAcceptingServer(
            new AllowAuth(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, pipe);
        var serverTask = server.AcceptOneAsync(cts.Token);

        // Le client d'abord : un échec rapide de sa part laissait le serveur attendre jusqu'aux 15 s, et le
        // test ne rapportait qu'une annulation (trois rouges en CI le 28/09, sans la raison du client).
        var resultat = await clientTask;
        if (!resultat.IsOk) cts.Cancel();
        resultat.IsOk.Should().BeTrue($"the connect-retry should reach the late-starting helper ({resultat.Error?.Code} : {resultat.Error?.Message})");
        await serverTask;
        exec.WasInvoked.Should().BeTrue();
    }
}
