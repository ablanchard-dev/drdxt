using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Tweaks.Hardware;

namespace DrDxt.App.ViewModels;

/// <summary>Un jeu de la liste « Changer » : <c>Exe</c> nul = automatique, le jeu ouvert.</summary>
public sealed record JeuPropose(string Libelle, string? Exe);

/// <summary>
/// Le jeu à optimiser, choisi par le joueur sous Optimiser. Automatique par défaut : le jeu ouvert, comme avant. Un jeu
/// choisi est celui que visent les réglages au profil d'un jeu (<see cref="JeuAOptimiser"/>), l'essai en alternance et la
/// protection du ping ; la mesure vise toujours le jeu affiché.
/// </summary>
public partial class ScanViewModel
{
    /// <summary>Le choix, dans les données du joueur. Null (tests) : automatique. Branché après la création de l'écran,
    /// qui a déjà lu la ligne : elle se redit.</summary>
    internal MemoireDuJeuChoisi? MemoireJeuChoisi
    {
        get => _memoireJeuChoisi;
        set { _memoireJeuChoisi = value; RedireLeJeuChoisi(); }
    }
    private MemoireDuJeuChoisi? _memoireJeuChoisi;

    /// <summary>Le jeu que visent les leviers ; l'App le branche, l'état de la ligne s'y lit.</summary>
    internal JeuAOptimiser? JeuAOptimiser
    {
        get => _jeuAOptimiser;
        set { _jeuAOptimiser = value; RedireLeJeuChoisi(); }
    }
    private JeuAOptimiser? _jeuAOptimiser;

    /// <summary>La ligne et son détail relus : au branchement, au choix, et à chaque scan (le jeu a pu s'ouvrir ou se fermer).</summary>
    private void RedireLeJeuChoisi()
    {
        OnPropertyChanged(nameof(LigneJeuChoisi));
        OnPropertyChanged(nameof(DetailJeuChoisi));
    }

    [ObservableProperty]
    private bool _listeDesJeuxOuverte;

    public string LabelChangerJeu => _localize("JeuChoisi.Changer");

    public string LigneJeuChoisi => MemoireJeuChoisi?.Lire() is { } choix
        ? string.Format(CultureInfo.CurrentCulture, _localize("JeuChoisi.Ligne"), NomDuJeu(choix.Exe))
        : _localize("JeuChoisi.Automatique");

    /// <summary>Ce que le choix ne peut pas faire maintenant, dit sous la ligne ; vide quand tout s'applique.</summary>
    public string DetailJeuChoisi
    {
        get
        {
            if (MemoireJeuChoisi?.Lire() is not { } choix || JeuAOptimiser is null) return string.Empty;
            var cle = JeuAOptimiser.Etat() switch
            {
                EtatDuJeuChoisi.Ferme => "JeuChoisi.Ferme",
                EtatDuJeuChoisi.SansChemin => "JeuChoisi.SansChemin",
                EtatDuJeuChoisi.CheminDisparu => "JeuChoisi.CheminDisparu",
                _ => null,
            };
            return cle is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, _localize(cle), NomDuJeu(choix.Exe));
        }
    }

    /// <summary>Automatique, puis les jeux des parties relevées, du plus joué au moins joué, puis le jeu ouvert s'il n'y est pas.</summary>
    public IReadOnlyList<JeuPropose> JeuxProposes
    {
        get
        {
            var parties = MemoireParties?.Lire() ?? [];
            var exes = parties.Where(p => DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(p.Jeu))
                .GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(p => p.MinutesJouees ?? (p.Fin - p.Debut).TotalMinutes))
                .Select(g => g.Key).ToList();
            if (ExeDuJeuOuvert() is { } ouvert && !exes.Contains(ouvert, StringComparer.OrdinalIgnoreCase)) exes.Add(ouvert);
            return [new JeuPropose(_localize("JeuChoisi.AutomatiqueCourt"), null), .. exes.Select(e => new JeuPropose(NomDuJeu(e), e))];
        }
    }

    /// <summary>Couture de test : le nom d'exe du jeu ouvert.</summary>
    internal Func<string?>? ExeDuJeuOuvertPourLeTest { get; set; }

    private string? ExeDuJeuOuvert()
    {
        if (ExeDuJeuOuvertPourLeTest is { } test) return test();
        return _gameLocator.LocateRunningGamePid() is { } pid && pid > 0 && NomExeDuProcessus(pid) is { } exe
               && DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(exe, CheminDuProcessus(pid)) ? exe : null;
    }

    [RelayCommand]
    private void ChangerJeu()
    {
        ListeDesJeuxOuverte = !ListeDesJeuxOuverte;
        if (ListeDesJeuxOuverte) OnPropertyChanged(nameof(JeuxProposes));
    }

    /// <summary>Le choix du joueur. Une protection du ping active suit le jeu qu'elle vise : éteinte sur l'ancien, puis
    /// rallumée sur le nouveau, par la même bascule qu'au clic (le service de démarrage l'oublie puis la retient).</summary>
    [RelayCommand]
    private async Task ChoisirJeu(string? exe)
    {
        if (MemoireJeuChoisi is null) return;
        var visaitLePing = ExeDuPing;
        // Le même jeu rechoisi garde le chemin déjà appris.
        var choix = exe is null ? null
            : MemoireJeuChoisi.Lire() is { } avant && string.Equals(avant.Exe, exe, StringComparison.OrdinalIgnoreCase) ? avant : new JeuChoisi(exe);
        if (MemoireJeuChoisi.Choisir(choix).IsOk && choix is not null)
            JeuAOptimiser?.RunningGameExecutablePath();   // le jeu choisi déjà ouvert apprend son chemin tout de suite
        ListeDesJeuxOuverte = false;
        RedireLeJeuChoisi();
        SignalerPlafondEnvoi();
        if (PlafondEnvoiActif && !string.Equals(visaitLePing, ExeDuPing, StringComparison.OrdinalIgnoreCase))
        {
            await BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            // Un retrait refusé laisse l'ancienne protection en place, et le dit : rien n'est rallumé par-dessus.
            if (!PlafondEnvoiActif) await BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        }
    }
}
