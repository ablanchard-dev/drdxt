using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// HelperServiceLoop : la boucle d'accept longue durée, multi-connexions, que le helper
// tient en mode --service. Elle rejoue le pipeline par connexion existant (un délégué
// AcceptOneAsync injecté) pendant toute la vie du service, sans changer ce que fait une
// connexion (négociation → op typée → fermeture).
//
// Invariants testés (avec un faux délégué d'accept : pas de tube, pas d'élévation) :
//   * de nombreuses connexions successives sont servies jusqu'à l'annulation (la boucle se réarme) ;
//   * une connexion corrompue (le délégué lève) est journalisée et tolérée : la boucle
//     passe à l'accept suivant (un seul mauvais client ne tue jamais le service) ;
//   * une rafale soutenue de fautes recule puis se ferme (s'arrête) plutôt que de
//     tourner en boucle serrée et brûler le processeur ;
//   * un accept propre remet à zéro le compteur de fautes consécutives (des ratés
//     passagers ne s'accumulent jamais jusqu'au seuil de fermeture) ;
//   * l'annulation se déroule proprement (aucune exception ne traverse la frontière).
// -----------------------------------------------------------------------
public sealed class HelperServiceLoopTests
{
    // Puits de diagnostics qui ne fait que compter les fautes journalisées.
    private sealed class CountingLog : IServiceLoopLog
    {
        public int Faults { get; private set; }
        public int Stops { get; private set; }
        public void Fault(int consecutive, int max, string detail) => Faults++;
        public void FailedClosed() => Stops++;
    }

    [Fact]
    public async Task Serves_many_sequential_connections_until_cancelled()
    {
        var served = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                if (Interlocked.Increment(ref served) >= 5)
                    cts.Cancel(); // s'arrêter après le 5e accept propre
                return Task.CompletedTask;
            },
            log: log,
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);

        served.Should().BeGreaterThanOrEqualTo(5, "the loop re-arms and serves connection after connection");
        code.Should().Be(0, "a cancelled service loop exits cleanly (0)");
        log.Faults.Should().Be(0);
    }

    [Fact]
    public async Task A_single_bad_connection_is_tolerated_and_the_loop_continues()
    {
        var attempt = 0;
        var goodAfterBad = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new IOException("garbage on the wire"); // un seul mauvais client
                // les accepts suivants sont propres ; on s'arrête une fois prouvé qu'on a continué
                if (Interlocked.Increment(ref goodAfterBad) >= 2)
                    cts.Cancel();
                return Task.CompletedTask;
            },
            log: log,
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);

        goodAfterBad.Should().BeGreaterThanOrEqualTo(2, "the loop kept serving after the bad connection");
        log.Faults.Should().Be(1, "the single fault was logged");
        log.Stops.Should().Be(0, "one fault must not fail the service closed");
        code.Should().Be(0);
    }

    [Fact]
    public async Task A_sustained_fault_burst_backs_off_then_fails_closed()
    {
        var backoffs = 0;
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ => throw new IOException("pipe name squatted"),
            log: log,
            backoff: _ => { backoffs++; return Task.CompletedTask; },
            maxConsecutiveFaults: 5);

        var code = await loop.RunAsync(CancellationToken.None);

        code.Should().Be(3, "after the fault burst the loop fails closed (exit 3), never spins forever");
        log.Stops.Should().Be(1);
        log.Faults.Should().Be(5);
        backoffs.Should().Be(4, "it backs off between faults but not after the final fail-closed one");
    }

    [Fact]
    public async Task A_clean_accept_resets_the_consecutive_fault_counter()
    {
        // Motif : faute, faute, propre, faute, faute… Avec un seuil de 3, l'accept propre doit
        // remettre le compteur à zéro pour qu'on n'atteigne jamais la fermeture par ratés passagers.
        var step = 0;
        var cleanCount = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                var s = step++;
                // 2 fautes, 1 propre, 2 fautes, 1 propre… jamais 3 fautes de suite.
                if (s % 3 == 2)
                {
                    if (Interlocked.Increment(ref cleanCount) >= 4)
                        cts.Cancel();
                    return Task.CompletedTask;
                }
                throw new IOException("transient");
            },
            log: log,
            backoff: _ => Task.CompletedTask,
            maxConsecutiveFaults: 3);

        var code = await loop.RunAsync(cts.Token);

        code.Should().Be(0, "the clean accepts reset the counter; it never reaches the fail-closed threshold");
        log.Stops.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_before_any_accept_exits_cleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var loop = new HelperServiceLoop(
            acceptOne: _ => Task.CompletedTask,
            log: new CountingLog(),
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_an_accept_unwinds_cleanly()
    {
        using var cts = new CancellationTokenSource();
        var loop = new HelperServiceLoop(
            acceptOne: async ct => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); },
            log: new CountingLog(),
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);
        code.Should().Be(0, "an OperationCanceledException from the accept is a clean shutdown, not a fault");
    }
}
