using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// 07/10/2026 (chantier J4) : un levier à chaud essayé une partie sur deux, sans geste à chaque partie. Le clic
/// « Essayer en alternance » est le consentement (et porte l'invite Windows éventuelle) ; ensuite DrDXT pose ou rend le
/// levier entre deux parties, SEULEMENT si son helper tourne déjà : jamais d'invite Windows pendant que tu joues. Helper
/// absent : l'essai se met en pause et la carte le dit. Tranché (<see cref="AlternanceDesEssais"/>), il s'arrête sur le
/// meilleur état ; « Arrêter l'essai » rend l'état d'origine à tout moment.
/// </summary>
public partial class ScanViewModel
{
    /// <summary><c>null</c> = pas d'essai possible (tests qui ne le vérifient pas).</summary>
    internal MemoireDeLEssai? MemoireEssai { get; set; }

    /// <summary>Le levier à chaud que le scan propose d'essayer, ou <c>null</c>.</summary>
    [ObservableProperty]
    private string? _levierAEssayer;

    /// <summary>La phrase de l'essai en cours, ou de son verdict ; <c>null</c> sans essai.</summary>
    [ObservableProperty]
    private string? _ligneEssai;

    /// <summary>L'avancée lancée par la dernière partie enregistrée, pour que les tests l'attendent.</summary>
    internal Task DerniereAvanceeEssai { get; private set; } = Task.CompletedTask;

    /// <summary>Seuls les leviers à chaud s'alternent : ils agissent tout de suite, sans redémarrage.</summary>
    internal static bool PeutAlterner(string id) => CatalogueDeCandidats.Decrire(id) is { Kind: CandidateKind.AutoHot };

    public bool EssaiEnCours => MemoireEssai?.Lire() is not null;

    /// <summary>Le bouton « Essayer en alternance » : un levier à essayer, et pas déjà un essai en cours.</summary>
    public bool EssaiProposable => LevierAEssayer is not null && MemoireEssai is not null && !EssaiEnCours;
    public string LabelEssaiBouton => _localize("Essai.Bouton");
    public string LabelEssaiArreter => _localize("Essai.Arreter");

    partial void OnLevierAEssayerChanged(string? value) => OnPropertyChanged(nameof(EssaiProposable));

    [RelayCommand]
    private async Task EssayerEnAlternance()
    {
        if (LevierAEssayer is { } id) await DemarrerEssaiAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ArreterEssai() => await ArreterEssaiAsync().ConfigureAwait(true);

    /// <summary>Le clic : la première partie l'a avec. Un levier déjà posé par DrDXT part de « posé ».</summary>
    internal async Task<bool> DemarrerEssaiAsync(string id)
    {
        if (MemoireEssai is null || !PeutAlterner(id) || LevierParId?.Invoke(id) is not { } levier || MemoireEssai.Lire() is not null)
            return false;
        var dejaPose = _journalLeviers?.Lire().Any(s => s.TweakId == id) == true;
        if (!dejaPose && !await PoserPourLEssaiAsync(levier).ConfigureAwait(true)) return false;
        MemoireEssai.Ecrire(new EssaiEnAlternance(id, dejaPose, true, Horloge(), DerniereBascule: Horloge()));
        DireEssai(MemoireParties?.Lire() ?? []);
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
        return true;
    }

    /// <summary>Le joueur arrête : l'état d'origine revient (le clic porte l'invite Windows éventuelle).</summary>
    internal async Task ArreterEssaiAsync()
    {
        // Revue F-38 : une bascule de fin de partie en cours se termine avant l'arrêt, qui lit alors l'état réel.
        await _verrouEssai.WaitAsync().ConfigureAwait(true);
        try { await ArreterEssaiSousVerrouAsync().ConfigureAwait(true); }
        finally { _verrouEssai.Release(); }
    }

    private readonly SemaphoreSlim _verrouEssai = new(1, 1);

    private async Task ArreterEssaiSousVerrouAsync()
    {
        if (MemoireEssai?.Lire() is not { } e) return;
        if (LevierParId?.Invoke(e.LevierId) is { } levier && e.PoseMaintenant != e.PoseALOrigine)
        {
            var ok = e.PoseALOrigine ? await PoserPourLEssaiAsync(levier).ConfigureAwait(true) : await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
            if (!ok) { LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ArretRate"), Titre(e.LevierId)); return; }
        }
        MemoireEssai.Ecrire(null);
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Arrete"), Titre(e.LevierId));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    /// <summary>La partie porte l'état de l'essai qui l'a vue jouer. Revue F-33 : seulement si elle a commencé après la
    /// dernière bascule (une partie à cheval ne compte d'aucun côté). F-37 : en pause, l'état est connu (rien n'a été
    /// touché), la partie compte. F-32 : un redémarrage depuis la dernière bascule rend l'état incertain (un levier de
    /// carte ne survit pas au redémarrage) : rien n'est marqué, l'essai s'arrête au prochain passage.</summary>
    internal PartieRelevee MarquerEssai(PartieRelevee p)
        => MemoireEssai?.Lire() is { } e && p.Debut >= e.Depuis && !RedemarreDepuis(e)
            ? p with { Essai = AlternanceDesEssais.Marque(e.LevierId, e.PoseMaintenant) }
            : p;

    private bool RedemarreDepuis(EssaiEnAlternance e) => DemarrageDuPc() > e.Depuis;

    /// <summary>Après une partie : l'état de la suivante. Poser ou rendre se fait sans invite Windows, ou pas du tout.</summary>
    internal async Task AvancerEssaiAsync(IReadOnlyList<PartieRelevee> parties)
    {
        await _verrouEssai.WaitAsync().ConfigureAwait(true);
        try { await AvancerEssaiSansFauteAsync(parties).ConfigureAwait(true); }
        catch (Exception)
        {
            // Revue F-30 : une faute ne se perd plus dans une tâche jamais observée. F-46 : elle a pu tomber après la
            // bascule, l'état réel n'est plus sûr ; l'essai s'arrête (en pause, ses parties seraient marquées d'un faux état).
            try
            {
                if (MemoireEssai?.Lire() is { } e)
                {
                    MemoireEssai.Ecrire(null);
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Incertain"), Titre(e.LevierId));
                    OnPropertyChanged(nameof(EssaiEnCours));
                    OnPropertyChanged(nameof(EssaiProposable));
                }
            }
            catch (Exception) { }
        }
        finally { _verrouEssai.Release(); }
    }

    private async Task AvancerEssaiSansFauteAsync(IReadOnlyList<PartieRelevee> parties)
    {
        if (MemoireEssai?.Lire() is not { } e || LevierParId?.Invoke(e.LevierId) is not { } levier) return;
        // Revue F-32 : après un redémarrage, l'état réel du levier n'est plus sûr ; l'essai s'arrête honnêtement.
        if (RedemarreDepuis(e))
        {
            // 09/10/2026 (revue) : un plan d'alimentation ou un réglage du pilote survit au redémarrage. L'essai ne s'efface
            // plus en laissant le levier posé (et un essai relancé l'aurait pris pour l'origine) : il rend d'abord l'origine,
            // sans invite Windows ; sinon il attend, et « Arrêter l'essai » la rend.
            if (e.PoseMaintenant != e.PoseALOrigine)
            {
                if (HelperJoignableSansUac is { } peut && levier.RequiresElevation && !await peut().ConfigureAwait(true))
                {
                    MemoireEssai.Ecrire(e with { EnPause = true });
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.RedemarrageARendre"), Titre(e.LevierId));
                    return;
                }
                await ArreterEssaiSousVerrouAsync().ConfigureAwait(true);
                if (MemoireEssai.Lire() is not null) return;   // rendu raté : « ArretRate » est dit, nouvel essai au passage suivant
            }
            MemoireEssai.Ecrire(null);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Redemarrage"), Titre(e.LevierId));
            OnPropertyChanged(nameof(EssaiEnCours));
            OnPropertyChanged(nameof(EssaiProposable));
            return;
        }
        var (pose, fini, verdict) = AlternanceDesEssais.Suivante(parties, e.LevierId, e.PoseALOrigine, e.Debut);
        if (pose != e.PoseMaintenant)
        {
            if (HelperJoignableSansUac is { } joignable && levier.RequiresElevation && !await joignable().ConfigureAwait(true))
            {
                MemoireEssai.Ecrire(e with { EnPause = true });
                LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Pause"), Titre(e.LevierId));
                return;
            }
            var ok = pose ? await PoserPourLEssaiAsync(levier).ConfigureAwait(true) : await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
            // Revue F-38 : « Annuler » pendant la bascule a arrêté l'essai ; ce qui vient d'être posé repart, rien ne revit.
            if (MemoireEssai.Lire() is null)
            {
                if (ok && pose) await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
                return;
            }
            if (!ok)
            {
                MemoireEssai.Ecrire(e with { EnPause = true });
                LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Pause"), Titre(e.LevierId));
                return;
            }
        }
        if (fini)
        {
            MemoireEssai.Ecrire(null);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize(verdict switch
            {
                VerdictEnJeu.Mieux => "Essai.Fini.Mieux",
                VerdictEnJeu.Pire => "Essai.Fini.Pire",
                _ => "Essai.Fini.Pareil",
            }), Titre(e.LevierId));
            OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
            return;
        }
        MemoireEssai.Ecrire(e with { PoseMaintenant = pose, EnPause = false, DerniereBascule = pose != e.PoseMaintenant ? Horloge() : e.DerniereBascule });
        DireEssai(parties);
    }

    /// <summary>Revue F-31 : le joueur annule le levier en essai depuis « Ce que DrDXT a posé » : l'essai s'arrête,
    /// rien ne le repose.</summary>
    internal void ArreterEssaiSurAnnulation(string id)
    {
        if (MemoireEssai?.Lire() is not { } e || e.LevierId != id) return;
        MemoireEssai.Ecrire(null);
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Arrete"), Titre(id));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    private void DireEssai(IReadOnlyList<PartieRelevee> parties)
    {
        if (MemoireEssai?.Lire() is not { } e) return;
        parties = parties.Where(p => p.Debut >= e.Debut).ToList();
        var avec = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, true));
        var sans = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, false));
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.EnCours"), Titre(e.LevierId), avec, sans,
            _localize(e.PoseMaintenant ? "Reglage.Allume" : "Reglage.Eteint"));
    }

    private async Task<bool> PoserPourLEssaiAsync(ITweak levier)
    {
        var r = await levier.ApplyAsync().ConfigureAwait(true);
        if (!r.IsOk || r.Value is null) return false;
        _journalLeviers?.Enregistrer(new ApplyReport([new ApplyOutcome(levier.Id, true, false, r.Value, null)], null));
        // Revue F-39 : un journal qui n'a rien gardé laisserait un levier posé, invisible et jamais rendu ; il repart.
        if (_journalLeviers is not null && _journalLeviers.Lire().All(s => s.TweakId != levier.Id))
        {
            await levier.RevertAsync(r.Value).ConfigureAwait(true);
            return false;
        }
        ChargerLeviersPoses();
        // GARDE1 (09/10/2026) : chaque pose de l'essai se date, comme celles d'Optimiser : la garde d'incident la couvre.
        NoterPoses([levier.Id], Horloge());
        return true;
    }

    private async Task<bool> RendrePourLEssaiAsync(ITweak levier)
    {
        // Revue F-31 : plus de trace au journal = déjà rendu (bouton Annuler) ; rien à rendre, pas un échec.
        // F-39 : seulement si le journal se lit ; illisible, rien ne dit que le levier est rendu.
        if (_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == levier.Id) is not { } snapshot) return _journalLeviers?.Lisible() ?? true;
        var r = await levier.RevertAsync(snapshot).ConfigureAwait(true);
        if (!r.IsOk) return false;
        _journalLeviers.Retirer(levier.Id);
        ChargerLeviersPoses();
        return true;
    }
}
