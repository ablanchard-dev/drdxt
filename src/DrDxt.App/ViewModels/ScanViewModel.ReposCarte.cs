using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>La carte Agir : le repos de la carte graphique, sa preuve et sa surveillance en jeu.</summary>
public sealed partial class ScanViewModel
{
    /// <summary>Une écriture « Normal » au global ne prend effet qu'au redémarrage : sa preuve, commencée avant, se
    /// conclut ici. Sans baisse nette, le réglage est retiré. Mesuré au lancement : un jeu déjà ouvert à ce moment
    /// fausse la mesure dans le sens du retrait, jamais dans celui d'un gain inventé.</summary>
    internal async Task ConclureReposCarteAsync(ITweak levier, IHardwareProbe sonde,
        DrDxt.Application.Tweaks.PreuvesEnAttente attente, Func<TimeSpan, Task> attendre)
    {
        // Lancée sans attente au démarrage : personne n'observerait son exception, et la preuve resterait « en attente ».
        try { await ConclureReposCarteSansFiletAsync(levier, sonde, attente, attendre); }
        catch (Exception ex) { VerdictReposCarte = RaisonLisible.Dire(ex, _localize); }
    }

    private async Task ConclureReposCarteSansFiletAsync(ITweak levier, IHardwareProbe sonde,
        DrDxt.Application.Tweaks.PreuvesEnAttente attente, Func<TimeSpan, Task> attendre)
    {
        // Au lancement, sans clic : un retrait du réglage ne lance pas le helper (SansGesteDuJoueur).
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        EcritureReposCarte = attente.Charger();
        var v = await DrDxt.Application.Tweaks.PreuveDuRepos.ConclureAsync(levier, sonde, 20, attendre, attente, EtatDeLaPose);
        if (v is null) return;
        var cle = v.Etat switch
        {
            DrDxt.Application.Tweaks.EtatDeLaPreuve.Prouve => "Carte.Repos.Prouve",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.PasDeGain => "Carte.Repos.PasDeGain",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.RetraitRate => "Carte.Repos.PreuveRetraitRate",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.ChangeDepuis => "Carte.Repos.ChangeDepuis",
            _ => "Carte.Repos.NonMesure",
        };
        VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize(cle), v.ApresW, v.AvantW);
        if (v.Etat != DrDxt.Application.Tweaks.EtatDeLaPreuve.Prouve || EcritureReposCarte is not { } e) return;
        // Prouvé, il reste : comme tout levier gardé, au journal des leviers (« Annuler » dans « Ce que DrDXT a posé ») et au
        // manifeste signé (la désinstallation le rend).
        var rapport = new ApplyReport([new ApplyOutcome(e.TweakId, true, false, new TweakSnapshot(e.TweakId, e.OriginalValueJson, e.AppliedValueJson), null)], null);
        _journalLeviers?.Enregistrer(rapport);
        if (_manifestMapper.ToRevertOnly(rapport) is { Count: > 0 } gardes
            && await _desiredStateStore.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), gardes)).ConfigureAwait(true) is { IsOk: false } refus)
            VerdictReposCarte += " " + string.Format(CultureInfo.CurrentCulture, _localize("Carte.Repos.PasInscrit"), RaisonLisible.Dire(refus, _localize));
        ChargerLeviersPoses();
        if (ReglagesDeLaCarte is not { } retenus) return;
        // Prouvé, mais le suivi en jeu ne s'est pas gardé : le réglage reste, sans la surveillance des parties, et c'est dit.
        try { retenus.Garder(new(e.TweakId, e.OriginalValueJson, e.AppliedValueJson, DateTimeOffset.Now)); }
        catch (IOException) { VerdictReposCarte += " " + _localize("Carte.Repos.SuiviNonGarde"); }
    }

    /// <summary>Ce qui garde le réglage prouvé au repos, et l'écriture qu'il annulerait. Posés au lancement.</summary>
    internal DrDxt.Application.Tweaks.ReglagesRetenus? ReglagesDeLaCarte { get; set; }
    internal ITweak? LevierReposCarte { get; set; }

    // ── Le geste, sur la page Carte graphique ───────────────────────────────────────────────────────────
    internal IHardwareProbe? SondeCarte { get; set; }
    internal DrDxt.Application.Tweaks.PreuvesEnAttente? AttenteReposCarte { get; set; }
    internal Func<TimeSpan, Task> AttendreCarte { get; set; } = t => Task.Delay(t);
    [ObservableProperty] private bool _reposCarteEnCours;
    [ObservableProperty] private bool _reposCarteEnAttente;
    [ObservableProperty] private string? _erreurReposCarte;

    /// <summary>La carte « Agir » du domaine graphique : le constat « performances maximales au global » lu, ou une
    /// preuve en cours ou conclue à dire.</summary>
    public bool ReposCarteVisible =>
        (LevierReposCarte is not null && _lastScan?.Findings.Any(f => f.TitleKey == "Finding.Gpu.PerfMaxGlobal") == true)
        || ReposCarteEnAttente || VerdictReposCarte is not null;
    // Sans sonde ni attente, le geste rendait la main en silence et Optimiser écrivait « rien n'a été
    // posé » pour une tentative qui n'avait pas eu lieu.
    public bool ReposCarteAOuvrir => ReposCarteVisible && SondeCarte is not null && AttenteReposCarte is not null
        && !ReposCarteEnAttente && !ReposCarteEnCours && VerdictReposCarte is null;
    partial void OnReposCarteEnAttenteChanged(bool value) { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    partial void OnReposCarteEnCoursChanged(bool value) { OnPropertyChanged(nameof(ReposCarteAOuvrir)); OperationLongueChangee(); }
    partial void OnVerdictReposCarteChanged(string? value) { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    internal void SignalerReposCarte() { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    public string LabelReposCarteTitre => _localize("Carte.Repos.Titre");
    public string LabelReposCarteDescription => _localize("Carte.Repos.Description");
    // La ligne qui reste à l'écran ; le texte entier passe en info-bulle.
    public string LabelReposCarteDescriptionCourte => _localize("Carte.Repos.Description.Court");
    public string LabelReposCarteMaintenir => _localize("Carte.Repos.Maintenir");
    public string LabelReposCarteEnCours => _localize("Carte.Repos.EnCours");
    public string LabelReposCarteEnAttente => _localize("Carte.Repos.EnAttente");
    public string LabelCapsuleReposCarte => _localize("Carte.Repos.Capsule");

    /// <summary>Premier temps de la preuve : 20 lectures du repos, l'écriture, la preuve gardée. Le second temps se
    /// joue au lancement qui suit un redémarrage (<see cref="ConclureReposCarteAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RendreLeReposALaCarte()
    {
        if (LevierReposCarte is not { } levier || SondeCarte is not { } sonde || AttenteReposCarte is not { } attente || ReposCarteEnCours) return;
        // Jeu ouvert, les lectures « avant » seraient celles de la charge : la preuve du lancement suivant mentirait.
        if (_gameLocator.JeuOuvert())
        {
            ErreurReposCarte = _localize("Carte.Repos.JeuOuvert");
            return;
        }
        ErreurReposCarte = null;
        ReposCarteEnCours = true;
        try
        {
            var r = await DrDxt.Application.Tweaks.PreuveDuRepos.EcrireAsync(levier, sonde, 20, AttendreCarte, attente);
            ReposCarteEnAttente = r.IsOk;
            if (!r.IsOk) ErreurReposCarte = RaisonLisible.OuIncertain(r, _localize("Carte.Repos.Refuse"), _localize);
        }
        // Posé, mais la preuve ne s'est pas gardée : la portée de pose reste ouverte, le journal du helper garde l'origine et
        // la rend au lancement suivant. L'App ne plante pas, et le dit.
        catch (IOException)
        {
            ReposCarteEnAttente = false;
            ErreurReposCarte = _localize("Carte.Repos.PreuveNonGardee");
        }
        finally { ReposCarteEnCours = false; }
    }

    /// <summary>Appelé quand la liste des parties change : surveille le réglage gardé, s'il y en a un.</summary>
    internal Task SurveillerSiPoseAsync(IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties) =>
        LevierReposCarte is { } l && ReglagesDeLaCarte is { } r ? SurveillerLaCarteEnJeuAsync(l, parties, r) : Task.CompletedTask;
    private DrDxt.Application.Tweaks.PreuveEnAttente? EcritureReposCarte { get; set; }

    /// <summary>Un réglage gardé au repos reste sous surveillance en jeu. Sur le jeu le plus joué depuis, trois
    /// parties toutes sous les trois d'avant (1 % le plus lent) : retiré, et dit. Sinon rien ne bouge. Changé depuis hors de
    /// DrDXT : rien n'est réécrit, il n'est plus suivi. « Retiré » ne se dit que relu à l'origine.</summary>
    internal async Task SurveillerLaCarteEnJeuAsync(ITweak levier, IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties,
        DrDxt.Application.Tweaks.ReglagesRetenus retenus)
    {
        // Lancée sans attente après chaque partie : son exception se dit au lieu de se perdre ; le réglage reste suivi.
        try { await SurveillerLaCarteEnJeuSansFiletAsync(levier, parties, retenus); }
        catch (Exception ex) { VerdictReposCarte = RaisonLisible.Dire(ex, _localize); }
    }

    private async Task SurveillerLaCarteEnJeuSansFiletAsync(ITweak levier, IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties,
        DrDxt.Application.Tweaks.ReglagesRetenus retenus)
    {
        // Après une partie relevée, sans clic : aucune invite Windows ; un retrait refusé reste suivi et se dit.
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        if (retenus.Charger() is not { } r || r.TweakId != levier.Id) return;
        // Rien ne s'écrit sans clic pendant qu'un jeu tourne : le retrait attend une fin de partie jeu fermé, le réglage reste suivi.
        if ((JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)()) return;
        var jeu = parties.Where(p => p.Debut >= r.Le).GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (jeu is null) return;
        // Jugé jusqu'au changement suivant, comme les bilans : un autre réglage, un pilote ou Windows Update fait après lui
        // porterait sa perte. Tant que les mises à jour de la machine ne sont pas lues, rien ne se retire.
        if (InstantsDesMisesAJour() is not { } machine) return;
        var suivant = (JournalChangements?.Lire() ?? []).Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true)
            .Select(c => c.Quand).Concat(machine).Where(q => q > r.Le + MemeInstant).Order()
            .Select(q => (DateTimeOffset?)q).FirstOrDefault();
        if (DrDxt.Application.Parties.ComparaisonDesParties.Juger(parties, r.Le, jeu, suivant) != DrDxt.Application.Parties.VerdictEnJeu.Pire) return;
        var snapshot = new TweakSnapshot(r.TweakId, r.OriginalValueJson, r.AppliedValueJson);
        if (EtatDeLaPose(snapshot) != Pose.DeDrDxt)
        {
            retenus.Effacer();
            await OublierAsync(snapshot.TweakId);
            VerdictReposCarte = _localize("Carte.Repos.ChangeDepuis");
            return;
        }
        var retrait = await levier.RevertAsync(snapshot);
        // Un retrait refusé, ou relu encore posé, laisse le réglage en place : il reste suivi (le retenu reste), et l'échec est dit.
        var relu = retrait.IsOk ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.Applied;
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Carte.Repos.RetraitRate"), jeu);
            return;
        }
        retenus.Effacer();
        await OublierAsync(snapshot.TweakId);
        VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize(
            relu == DrDxt.Application.Tweaks.EtatApplique.NotApplied ? "Carte.Repos.PireEnJeu" : "Carte.Repos.PireEnJeuNonRelu"), jeu);
    }

    /// <summary>Un levier qui n'est plus celui de DrDXT (retiré, rendu, ou changé depuis) : il sort du manifeste, puis du journal des
    /// leviers.</summary>
    private async Task OublierAsync(string id)
    {
        await RetirerDuManifesteAsync(id).ConfigureAwait(true);
        _journalLeviers?.Retirer(id);
        ChargerLeviersPoses();
    }

    /// <summary>La relecture apres la console : la meme lecture que le scan (DISM /CheckHealth, jamais d'UAC).</summary>
    internal Func<RepairFinding> RelireSanteWindows { get; set; } = () => DrDxt.App.Platform.SanteWindows.Constat();
}
