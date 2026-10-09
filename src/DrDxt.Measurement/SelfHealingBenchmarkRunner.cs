using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Le produit repare son propre service de mesure, au lieu de le demander a l'utilisateur.
///
/// `InstallPresentMonServiceOp` existait, etait allowlistee, etait testee de bout en bout —
/// et **aucun code du produit ne l'emettait**. Le service se demarrait a la main sur la machine de
/// reference, pendant que le plan de l'installeur (non publie) affirmait « l'app demarre le service
/// quand une mesure est demandee ». C'est cette phrase qui a couvert le trou pendant des mois.
///
/// Consequence concrete, et pas theorique : une machine ou DrDXT a ete installe avant le lot du
/// tube prive porte encore l'ancien `binPath`, donc le tube par defaut, donc celui du service
/// Intel. Rien dans le produit ne pouvait la reparer — <see cref="PresentMonServicePlan"/> sait
/// decider `Repair`, mais personne ne le lui demandait.
///
/// ══ la regle, et sa borne ══
/// **Un seul echec justifie une elevation** : `MEAS.PMON_UNAVAIL`, le service qui ne repond pas.
/// Ni l'absence de jeu, ni un jeu protege, ni une capture vide — reveiller l'UAC pour ceux-la
/// serait du bruit, et le bruit finit par etre clique sans etre lu.
/// **Une seule tentative** : un service durablement casse ferait sinon boucler l'UAC a chaque scan.
/// **L'echec rendu reste celui de la mesure**, jamais celui du helper : c'est la mesure que
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

        // Installe le service s'il manque, REPARE sa ligne de commande si elle n'est pas la notre,
        // puis le demarre. Les trois cas sont tranches par PresentMonServicePlan, cote eleve.
        var reparation = await _eleve.ExecuteAsync(new InstallPresentMonServiceOp(), ct);
        if (!reparation.IsOk)
            return premier;

        return await _interne.RunAsync(targetPid, duration, runs, ct);
    }
}
