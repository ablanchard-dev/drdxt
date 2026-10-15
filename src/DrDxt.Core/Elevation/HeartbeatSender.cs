namespace DrDxt.Core;

// -----------------------------------------------------------------------
// HeartbeatSender : un petit objet jetable et réutilisable qui maintient en vie le bail
// d'un levier tenu en le renouvelant à intervalle régulier, jusqu'à sa libération.
//
// Contrat (le chemin d'application à décalage tenu est conçu pour en brancher un) :
// tant qu'un décalage GPU ou une limite de puissance tenus sont posés et que le contrôleur
// vit, le battement renouvelle le bail pour que l'homme mort du helper ne parte jamais.
// Quand l'application finit (ou que le processus qui pilote meurt ou le libère), le
// battement s'arrête ; le bail expire et le chien de garde annule le levier tenu.
//
// État : pas encore branché sur le vrai chemin d'application. Les leviers GPU n'arment
// pas de bail (voir la note sur le bail dans GpuUndervoltTweak) ; ce sender et le
// LeaseWatchdog ne servent qu'aux tests. Une fois branché, le contrat ci-dessus tient.
//
// Le renouvellement passe par IElevatedClient.RenewLease → le même pipeline IPC signé et
// la même identité d'appelant que toute autre op (le helper ne laisse que le détenteur du
// bail le renouveler). Un échec passager (pipe occupé, helper absent un instant) est
// avalé : un battement manqué ne doit pas faire planter le contrôleur ; le bail a
// simplement moins de battements avant sa vraie échéance.
// -----------------------------------------------------------------------
public sealed class HeartbeatSender : IAsyncDisposable
{
    private readonly IElevatedClient _client;
    private readonly string _leaseId;
    private readonly int _leaseSeconds;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private volatile bool _disposed;
    private int _perdu;

    /// <summary>Le helper a refusé un renouvellement (il ne connaît plus ce bail : il a
    /// expiré et l'origine a été rendue). Levé une fois. Une erreur passagère du transport ne le lève pas.</summary>
    public event Action? Perdu;

    /// <param name="client">Le client élevé qui envoie le renouvellement.</param>
    /// <param name="leaseId">Le bail à maintenir (l'id de l'EstablishLeaseOp).</param>
    /// <param name="leaseSeconds">La prolongation demandée à chaque renouvellement.</param>
    /// <param name="interval">Cadence de fond, facultative. Par défaut ~1/3 du bail, pour
    /// tolérer quelques ratés avant une vraie échéance.</param>
    public HeartbeatSender(IElevatedClient client, string leaseId, int leaseSeconds, TimeSpan? interval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(leaseId))
            throw new ArgumentException("Lease id must be non-empty.", nameof(leaseId));
        if (leaseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds), "Lease duration must be positive.");

        _leaseId = leaseId;
        _leaseSeconds = leaseSeconds;
        _interval = interval ?? TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 3));
    }

    /// <summary>Envoie un seul renouvellement, maintenant. Ne fait rien après libération. Ne
    /// lève jamais : un échec passager est avalé (le battement suivant réessaie).</summary>
    public async Task PulseAsync(CancellationToken ct)
    {
        if (_disposed || Volatile.Read(ref _perdu) == 1)
            return;
        try
        {
            var r = await _client.RenewLease(_leaseId, _leaseSeconds, ct).ConfigureAwait(false);
            // « Rendu par le helper » : Perdu (le journal peut être levé). « Inconnu » (helper relancé, registre vide) : le
            // réglage est peut-être encore posé, pas de Perdu, mais plus rien à renouveler. Conséquence assumée : jusqu'à la
            // fin de la session, ce réglage n'a plus de bail ; seul le journal le rend, au démarrage suivant de l'App.
            // Un refus d'appelant (ElevationDenied) est passager : le battement continue.
            if (r is { IsOk: false, Error.Code: ErrorCodes.LeaseReverted or ErrorCodes.LeaseUnknown } && Interlocked.Exchange(ref _perdu, 1) == 0)
            {
                try { _cts.Cancel(); } catch { /* déjà libéré */ }
                if (r.Error?.Code == ErrorCodes.LeaseReverted) Perdu?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt ou annulation par l'appelant : pas une erreur de battement.
        }
        catch
        {
            // Une erreur de transport passagère ne doit pas faire planter le contrôleur.
        }
    }

    /// <summary>Lance la boucle de renouvellement en fond, à l'intervalle choisi. Idempotent.
    /// Chaque tic envoie un battement jusqu'à la libération du sender.</summary>
    public void Start()
    {
        if (_disposed || _loop is not null)
            return;
        _loop = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await PulseAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal à la libération.
        }
    }

    /// <summary>L'essai est rendu, le bail n'a plus rien à garder. Le battement s'arrête
    /// et le bail est ramené à une seconde : il expire aussitôt (son retour rejoue un état déjà rendu) et le helper peut
    /// s'arrêter avec l'App au lieu d'attendre l'expiration.</summary>
    public async Task LibererAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        try { await _client.RenewLease(_leaseId, 1, CancellationToken.None).ConfigureAwait(false); }
        catch { /* bail laissé à son expiration : le helper rend l'origine seul */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* already disposed */ }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* loop swallows its own; defensive */ }
        }

        _cts.Dispose();
    }
}
