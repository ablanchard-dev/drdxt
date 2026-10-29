using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Le produit repare son propre service de mesure : ce decorateur emet `InstallPresentMonServiceOp` (allowlistee, testee
/// de bout en bout), et <see cref="PresentMonServicePlan"/> decide `Repair` pour une installation plus ancienne dont le
/// `binPath` vise encore le tube par defaut, celui du service Intel.
/// Un seul echec justifie une elevation : `MEAS.PMON_UNAVAIL`, le service qui ne repond pas ; ni l'absence de jeu, ni un
/// jeu protege, ni une capture vide (une UAC de trop finit cliquee sans etre lue). Une tentative par session : un refus
/// est retenu en memoire, jamais sur disque, et le scan suivant le dit ; relancer DrDXT reessaie. L'echec rendu reste
/// celui de la mesure, dont le code choisit le message.
/// </summary>
public sealed class SelfHealingBenchmarkRunner : IBenchmarkRunner
{
    private readonly IBenchmarkRunner _interne;
    private readonly IElevatedClient _eleve;
    private volatile bool _reparationRefusee;

    public SelfHealingBenchmarkRunner(IBenchmarkRunner interne, IElevatedClient eleve)
    {
        _interne = interne;
        _eleve = eleve;
    }

    /// <inheritdoc/>
    public async Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct)
    {
        var premier = await _interne.RunAsync(targetPid, duration, runs, ct);
        if (premier.IsOk || premier.Error!.Value.Code != ErrorCodes.PmonUnavailable)
            return premier;
        if (_reparationRefusee)
            return Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, Docteur.T("SH.DejaRefusee"));

        // Installer, reparer ou demarrer : tranche par PresentMonServicePlan, cote eleve.
        var reparation = await _eleve.ExecuteAsync(new InstallPresentMonServiceOp(), ct);
        if (!reparation.IsOk)
        {
            // Un helper injoignable (la veille, sans geste du joueur) n'a rien refusé : seul un vrai refus est retenu.
            if (reparation.Error?.Code != ErrorCodes.HelperUnreachable) _reparationRefusee = true;
            return premier;
        }

        return await _interne.RunAsync(targetPid, duration, runs, ct);
    }
}
