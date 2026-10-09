using CommunityToolkit.Mvvm.ComponentModel;

namespace DrDxt.App.ViewModels;

// Le jeu pendant la mesure : l'attendre quand il est réduit, et vérifier qu'il reste à l'écran tant
// qu'on le mesure (26/09/2026).
public sealed partial class ScanViewModel
{
    // La durée est dite au joueur : revenir sur DrDXT avant la fin réduit le jeu en pleine capture, et les
    // images d'un jeu réduit se mêleraient à celles de la partie.
    public string LabelAttenteDuJeu => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        _localize("Scan.AttenteJeu"), DureeCaptureEnJeu.TotalSeconds);
    public string LabelAttenteDuJeuSession => _localize("Apply.AttenteJeu");
    internal static readonly TimeSpan DureeCaptureEnJeu = TimeSpan.FromSeconds(20);

    // 26/09/2026 : un plein écran exclusif sur un seul écran est réduit dès qu'on clique sur DrDXT. Le scan
    // ne trouvait alors jamais le jeu et disait « lance une partie » à un joueur en pleine partie. Il
    // attend maintenant que le jeu revienne à l'écran, et le dit.
    [ObservableProperty] private bool _attenteDuJeu;
    [ObservableProperty] private bool _attenteDuJeuSession;
    internal static readonly TimeSpan DelaiAttenteDuJeu = TimeSpan.FromSeconds(45);
    internal const int AbsencesAvantAbandon = 3;
    internal Func<TimeSpan, Task> Patienter { get; set; } = Task.Delay;

    private sealed class SurveillanceDuJeu { public volatile bool Fini, Quitte; }

    // Bornée ; la mesure finie, elle s'arrête au pas suivant sans être attendue.
    private async Task SurveillerLeJeuAsync(int pid, SurveillanceDuJeu s, TimeSpan borne)
    {
        try
        {
            for (var i = 0; i < borne.TotalSeconds && !s.Fini; i++)
            {
                await Patienter(TimeSpan.FromSeconds(1));
                if (s.Fini) return;
                if (_gameLocator.LocateRunningGamePid() != pid && !_gameLocator.EstAffiche(pid)) { s.Quitte = true; return; }
            }
        }
        catch
        {
            // Une surveillance en panne ne jette pas une capture : elle n'accuse rien.
        }
    }

    private async Task<int?> AttendreLeJeuAsync(bool pourLaSession = false)
    {
        if (pourLaSession) AttenteDuJeuSession = true; else AttenteDuJeu = true;
        try
        {
            var pas = TimeSpan.FromSeconds(1);
            var absences = 0;
            for (var attendu = TimeSpan.Zero; attendu < DelaiAttenteDuJeu; attendu += pas)
            {
                await Patienter(pas);
                if (_gameLocator.LocateRunningGamePid() is { } pid) return pid;
                // Fermé pendant l'attente : rien à attendre. Mais pas sur un seul relevé : une fenêtre qui sort de
                // l'état réduit n'est, un instant, ni réduite ni à la taille de l'écran (vu sur le vrai Windows,
                // sous charge : l'attente abandonnait un jeu en train de revenir).
                absences = _gameLocator.JeuOuvert() ? 0 : absences + 1;
                if (absences >= AbsencesAvantAbandon) return null;
            }
            return null;
        }
        finally { AttenteDuJeu = false; AttenteDuJeuSession = false; }
    }
}
