using System.Runtime.Versioning;

namespace DrDxt.ElevatedHelper;

// HelperServiceLoop : la boucle d'acceptation durable, multi-connexion, du helper élevé en mode --service. Elle rappelle
// à chaque tour le délégué AcceptOneAsync (HandshakeAcceptingServer.AcceptOneAsync) : (re)créer le pipe
// FIRST_PIPE_INSTANCE protégé par ACL → accepter → handshake (auth → clé éphémère) → servir une op typée → fermer. Elle
// n'ajoute que « boucler, réarmer, durcir », sans toucher au protocole (HMAC, nonce, codec, allowlist, exécuteur) :
//   * une seule connexion mauvaise ou déchet (le délégué lève) est journalisée et la boucle
//     continue : un mauvais client ne tue jamais le service ;
//   * une rafale de pannes soutenue recule entre les tentatives puis se ferme (fail-closed, rend
//     un code non nul) au lieu de tourner dans une boucle serrée qui brûle le CPU ;
//   * une acceptation propre remet à zéro le compteur de pannes consécutives (les accrocs passagers
//     ne s'accumulent jamais jusqu'au seuil) ;
//   * l'annulation (le signal d'arrêt) se déroule proprement : une OperationCanceledException
//     venue de l'acceptation est un arrêt, pas une panne.
// Acceptation et recul sont des coutures (testée avec des faux, sans pipe ni élévation) ; Program.cs branche le vrai
// AcceptOneAsync et un recul Task.Delay.

/// <summary>Récepteur des diagnostics de la boucle d'acceptation du service. Minuscule et
/// injectable pour tester la boucle ; en production, il écrit sur la console / les diagnostics du helper.</summary>
public interface IServiceLoopLog
{
    /// <summary>Une acceptation en panne (journalisée, la boucle continue).</summary>
    void Fault(int consecutive, int max, string detail);

    /// <summary>Le seuil de pannes est atteint ; la boucle s'arrête en fail-closed.</summary>
    void FailedClosed();
}

[SupportedOSPlatform("windows")]
public sealed class HelperServiceLoop
{
    /// <summary>Code de sortie quand la boucle se ferme après une rafale de pannes soutenue.</summary>
    public const int FailClosedExitCode = 3;

    private readonly Func<CancellationToken, Task> _acceptOne;
    private readonly IServiceLoopLog _log;
    private readonly Func<CancellationToken, Task> _backoff;
    private readonly int _maxConsecutiveFaults;

    /// <param name="acceptOne">Sert exactement une connexion (le pipeline par connexion
    /// éprouvé). Rappelé à chaque tour pour servir la connexion suivante.</param>
    /// <param name="log">Récepteur des diagnostics de panne / de fermeture.</param>
    /// <param name="backoff">Attendu entre deux pannes consécutives pour éviter une boucle serrée
    /// (en production, un court Task.Delay ; les tests passent un no-op ou un compteur).</param>
    /// <param name="maxConsecutiveFaults">Seuil de fermeture : ce nombre de pannes d'affilée
    /// (sans acceptation propre entre elles) arrête la boucle.</param>
    public HelperServiceLoop(
        Func<CancellationToken, Task> acceptOne,
        IServiceLoopLog log,
        Func<CancellationToken, Task> backoff,
        int maxConsecutiveFaults = 20)
    {
        _acceptOne = acceptOne ?? throw new ArgumentNullException(nameof(acceptOne));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        if (maxConsecutiveFaults < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFaults));
        _maxConsecutiveFaults = maxConsecutiveFaults;
    }

    /// <summary>Fait tourner la boucle d'acceptation jusqu'à l'annulation ou une rafale de pannes.
    /// Rend 0 sur un arrêt propre (annulé), ou <see cref="FailClosedExitCode"/> si elle s'est
    /// fermée. Ne lève jamais à travers la frontière pour une panne au niveau d'une connexion.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var consecutiveFaults = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _acceptOne(ct).ConfigureAwait(false);
                consecutiveFaults = 0; // une acceptation propre remet le compteur de pannes à zéro
            }
            catch (OperationCanceledException)
            {
                break; // signal d'arrêt pendant l'acceptation → arrêt propre, pas une panne
            }
            catch (Exception ex)
            {
                // Panne d'une connexion (déchet sur le fil, squatteur de pipe…) : journaliser, reculer, et si elle
                // persiste s'arrêter en fail-closed plutôt que brûler le CPU.
                consecutiveFaults++;
                _log.Fault(consecutiveFaults, _maxConsecutiveFaults, ex.GetType().Name);

                if (consecutiveFaults >= _maxConsecutiveFaults)
                {
                    _log.FailedClosed();
                    return FailClosedExitCode;
                }

                try
                {
                    await _backoff(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return 0;
    }
}
