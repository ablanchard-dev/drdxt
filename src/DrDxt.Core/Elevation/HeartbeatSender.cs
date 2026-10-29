namespace DrDxt.Core;

// HeartbeatSender : renouvelle à intervalle régulier le bail d'un levier tenu, jusqu'à sa libération. Tant qu'un
// décalage GPU ou une limite de puissance tenus sont posés et que le contrôleur vit, l'homme mort du helper ne part pas ;
// quand l'application finit (ou que son processus meurt), le battement s'arrête, le bail expire et le chien de garde
// annule le levier. Branché sur le mode Expert de la carte (décalage, ventilateur) et sur le plafond d'envoi.
// IElevatedClient.RenewLease : même pipeline IPC signé, même identité que toute op (seul le détenteur renouvelle). Un
// échec passager (pipe occupé, helper absent un instant) est avalé : un battement de moins avant l'échéance.
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
    private int _injoignable;

    /// <summary>Le helper a refusé un renouvellement (il ne connaît plus ce bail : il a
    /// expiré et l'origine a été rendue). Levé une fois. Une erreur passagère du transport ne le lève pas.</summary>
    public event Action? Perdu;

    /// <summary>Le helper ne répond plus (transport en échec) ou ne connaît plus ce bail (relancé, registre vide) : ce qu'il
    /// gardait en mémoire, l'homme mort et ses gardes, est mort avec lui, et le réglage est peut-être encore posé. Levé une
    /// fois. Le levier qui ne doit jamais rester posé sans garde (le ventilateur fixe) rend lui-même son origine.</summary>
    public event Action? Injoignable;

    /// <param name="client">Le client élevé qui envoie le renouvellement.</param>
    /// <param name="leaseId">Le bail à maintenir (l'id de l'EstablishLeaseOp).</param>
    /// <param name="leaseSeconds">La prolongation demandée à chaque renouvellement.</param>
    /// <param name="interval">Cadence de fond, facultative. Par défaut ~1/3 du bail, pour
    /// tolérer quelques ratés avant une vraie échéance.</param>
    public HeartbeatSender(IElevatedClient client, string leaseId, int leaseSeconds, TimeSpan? interval = null)
    {
        // Jamais de lancement du helper par un battement : une invite Windows sans geste du joueur, et refusée, elle
        // reviendrait à chaque battement. Un helper absent se dit par Injoignable.
        _client = (client ?? throw new ArgumentNullException(nameof(client))).SansRelance;
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
            // réglage est peut-être encore posé, pas de Perdu, plus de bail jusqu'à la fin de la session ; seul le journal
            // le rend, au démarrage suivant de l'App. Un refus d'appelant (ElevationDenied) est passager : on continue.
            if (r is { IsOk: false, Error.Code: ErrorCodes.LeaseReverted or ErrorCodes.LeaseUnknown } && Interlocked.Exchange(ref _perdu, 1) == 0)
            {
                try { _cts.Cancel(); } catch { /* déjà libéré */ }
                if (r.Error?.Code == ErrorCodes.LeaseReverted) Perdu?.Invoke();
            }
            if (r is { IsOk: false, Error.Code: ErrorCodes.HelperUnreachable or ErrorCodes.LeaseUnknown }) LeverInjoignable();
        }
        catch (OperationCanceledException)
        {
            // Arrêt ou annulation par l'appelant : pas une erreur de battement.
        }
        catch
        {
            // Une erreur de transport ne fait pas planter le contrôleur ; elle se dit à qui doit rendre son réglage.
            LeverInjoignable();
        }
    }

    private void LeverInjoignable()
    {
        if (Interlocked.Exchange(ref _injoignable, 1) == 0) Injoignable?.Invoke();
    }

    /// <summary>Lance la boucle de renouvellement en fond, à l'intervalle choisi. Idempotent.
    /// Chaque tic envoie un battement jusqu'à la libération du sender.</summary>
    public void Start()
    {
        if (_disposed || _loop is not null)
            return;
        // La boucle ne prend rien du flux qui la lance : une portée de pose ouverte autour de la pose ne la suit pas.
        using (ExecutionContext.SuppressFlow())
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
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
