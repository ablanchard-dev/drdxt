using DrDxt.Measurement.Network;

namespace DrDxt.Application.Network;

/// <summary>
/// 27/09/2026 : « Protéger mon ping » sans scan pendant une partie saturée.
///
/// Avant, la protection ne s'allumait qu'apres un scan lance pendant une partie ou l'envoi avait
/// sature : chez un joueur normal, quasiment jamais. Allumer la veille ne pose rien. Elle regarde les
/// vraies parties (le jeu repere par le produit), accumule les echantillons d'envoi et de ping partie
/// apres partie, et les juge avec le meme juge que le scan (<see cref="Bufferbloat.Juger"/>). Le
/// plafond ne nait que d'un verdict « detecte » sur SES parties : jamais d'une valeur par defaut.
/// Tant que l'envoi ne sature pas, elle ne trouve rien et ne touche a rien.
///
/// Les echantillons ne se melangent pas d'un jeu a l'autre : le plafond vise un exe precis.
/// </summary>
public sealed class VeilleDuPing
{
    /// <summary>Au-dela, les plus anciens sortent : une veille de plusieurs jours ne grossit pas sans fin.</summary>
    public const int MaxEchantillons = 20_000;

    private readonly Func<string?> _jeuEnCours;
    private readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<EchantillonEnvoi>>> _collecter;
    private readonly Func<TimeSpan, CancellationToken, Task> _attendre;
    private readonly TimeSpan _fenetre;
    private readonly List<EchantillonEnvoi> _echantillons = new();
    private string? _exe;

    public VeilleDuPing(
        Func<string?> jeuEnCours,
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<EchantillonEnvoi>>> collecter,
        TimeSpan fenetre,
        Func<TimeSpan, CancellationToken, Task>? attendre = null)
    {
        _jeuEnCours = jeuEnCours;
        _collecter = collecter;
        _fenetre = fenetre;
        _attendre = attendre ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Le dernier jugement, pour l'ecran (« rien encore : 12 echantillons en charge sur 20 »).</summary>
    public VerdictBufferbloat? DernierVerdict { get; private set; }

    public int EchantillonsRetenus => _echantillons.Count;

    /// <summary>Veille jusqu'a un bufferbloat detecte en partie, puis rend le jeu et le verdict.
    /// Hors partie, attend une fenetre sans rien mesurer. L'annulation remonte.</summary>
    public async Task<(string Exe, VerdictBufferbloat Verdict)> AttendreUnBufferbloatAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var exe = _jeuEnCours();
            if (exe is null)
            {
                await _attendre(_fenetre, ct).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(exe, _exe, StringComparison.OrdinalIgnoreCase))
            {
                _exe = exe;
                _echantillons.Clear();
            }

            var fenetre = await _collecter(_fenetre, ct).ConfigureAwait(false);
            // La partie a pu se fermer pendant la fenetre : ce qu'elle a vu apres ne decrit plus le jeu.
            if (!string.Equals(_jeuEnCours(), exe, StringComparison.OrdinalIgnoreCase)) continue;

            _echantillons.AddRange(fenetre);
            if (_echantillons.Count > MaxEchantillons)
                _echantillons.RemoveRange(0, _echantillons.Count - MaxEchantillons);

            var verdict = Bufferbloat.Juger(_echantillons);
            DernierVerdict = verdict;
            if (verdict.Detecte) return (exe, verdict);
        }
    }
}
