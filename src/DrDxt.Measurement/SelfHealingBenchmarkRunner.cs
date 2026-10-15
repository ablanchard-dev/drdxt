using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Le produit repare son propre service de mesure, au lieu de le demander a l'utilisateur.
///
/// `InstallPresentMonServiceOp` est allowlistee et testee de bout en bout ; ce decorateur est le
/// code du produit qui l'emet. Sans lui, le service se demarrerait a la main.
///
/// Consequence concrete, et pas theorique : une installation plus ancienne de DrDXT, d'avant le
/// tube prive, porte encore l'ancien `binPath`, donc le tube par defaut, donc celui du service
/// Intel. <see cref="PresentMonServicePlan"/> sait decider `Repair` ; ce decorateur le lui demande.
///
/// ══ la regle, et sa borne ══
/// Un seul echec justifie une elevation : `MEAS.PMON_UNAVAIL`, le service qui ne repond pas.
/// Ni l'absence de jeu, ni un jeu protege, ni une capture vide : reveiller l'UAC pour ceux-la
/// serait du bruit, et le bruit finit par etre clique sans etre lu.
/// Une seule tentative : un service durablement casse ferait sinon boucler l'UAC a chaque scan.
/// L'echec rendu reste celui de la mesure, jamais celui du helper : c'est la mesure que
/// l'appelant a demandee, et c'est son code d'erreur qui choisit le message affiche.
/// </summary>
public sealed class SelfHealingBenchmarkRunner : IBenchmarkRunner
{
    private readonly IBenchmarkRunner _interne;
    private readonly IElevatedClient _eleve;

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

        // Installe le service s'il manque, repare sa ligne de commande si elle n'est pas la notre,
        // puis le demarre. Les trois cas sont tranches par PresentMonServicePlan, cote eleve.
        var reparation = await _eleve.ExecuteAsync(new InstallPresentMonServiceOp(), ct);
        if (!reparation.IsOk)
            return premier;

        return await _interne.RunAsync(targetPid, duration, runs, ct);
    }
}
