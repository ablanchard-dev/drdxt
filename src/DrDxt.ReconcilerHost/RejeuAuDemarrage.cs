using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

/// <summary>Le journal d'écriture anticipée du service, ouvert une fois (sa racine réparée d'abord) ; null quand la racine
/// n'a pas pu l'être.</summary>
public sealed record JournalDuService(JournalDEcritureAnticipee? Journal);

/// <summary>La fin du rejeu au démarrage, pour le Worker : une attente, sans aucun chemin d'écriture.</summary>
public sealed record FinDuRejeu(Func<Task> Attendre);

/// <summary>
/// Le rejeu du journal au démarrage du service, avant la première réconciliation. Le service démarre aussi pendant une partie
/// (la tâche cochée à l'installation, une récupération après un plantage, sc start) : le rejeu attend alors la fin du jeu,
/// comme la passe quotidienne du Worker, au plus <see cref="Worker.AttenteEnPartieMax"/>. Cette attente ne retient pas le démarrage, que le gestionnaire de services borne à
/// quelques dizaines de secondes : StartAsync la lance en fond et rend la main, et le Worker l'attend avant de réconcilier.
/// Sous SYSTEM, une écriture de la ruche du joueur attend le helper de sa session ; une écriture que le service ne sait pas
/// faire (la carte) reste en suspens pour lui.
/// </summary>
/// <param name="ecrireLeRapport">Le dernier rapport non vide, écrit pour l'App.</param>
/// <param name="jeuEnCours">Le même filtre que la passe quotidienne du Worker.</param>
public sealed class RejeuAuDemarrage(JournalDuService journal, ILectureDesCibles lecture, IElevatedClient ecriture,
    IDiagnosticsLog? diagnostics = null, Action<RapportDuRendu>? ecrireLeRapport = null, Func<bool>? jeuEnCours = null,
    IDelay? delai = null) : IHostedService
{
    public static readonly TimeSpan Borne = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _arret = new();
    private readonly Func<bool> _jeuEnCours = jeuEnCours ?? Worker.JeuConnuLance;
    private readonly IDelay _delai = delai ?? new SystemDelay();

    /// <summary>L'attente de fin de jeu puis le rejeu ; le Worker l'attend avant sa première réconciliation.</summary>
    public Task Differe { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Differe = Task.Run(() => AttendreEtRejouerAsync(_arret.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _arret.Cancel();
        try { await Differe.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* l'hôte s'arrête : le rejeu suivant reprendra */ }
    }

    private async Task AttendreEtRejouerAsync(CancellationToken ct)
    {
        try
        {
            await Worker.AttendreLaFinDuJeuAsync(_jeuEnCours, _delai, ct).ConfigureAwait(false);
            using var borne = CancellationTokenSource.CreateLinkedTokenSource(ct);
            borne.CancelAfter(Borne);
            var rapport = await JournalDuHelper.RejouerAsync(journal.Journal, lecture, ecriture, RejeuDuJournal.ProcessusVivant,
                RejeuDuJournal.CompteCourant, borne.Token).ConfigureAwait(false);
            foreach (var l in rapport?.Lignes ?? [])
                diagnostics?.Change(RejeuDuJournal.Source, l.Cible, l.Issue.ToString(), "DrDXT");
            if (rapport is { Lignes.Count: > 0 } && ecrireLeRapport is not null) ecrireLeRapport(rapport);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* service arrêté pendant l'attente */ }
        catch (Exception)
        {
            // Un rejeu qui échoue n'empêche pas le service de démarrer : le suivant le retente.
            diagnostics?.Change(RejeuDuJournal.Source, "-", "replay-failed", "DrDXT");
        }
    }
}
